using System.Runtime.ExceptionServices;
using System.Text.Json;
using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Setup;

/// <summary>A region installed in the packages folder: its release, and whether its overlays came with it.</summary>
internal sealed record InstalledRegion(string Id, string Release, bool Overlays, DateTime InstalledUtc);

/// <summary>How far an install is: what it does now, and the bytes downloaded of all it downloads.</summary>
internal sealed record InstallProgress(string Stage, long Done, long Total);

/// <summary>An install that cannot go on; the message says why, in the window's words.</summary>
internal sealed class InstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Installs, updates and deletes the regions of AutoOrtho's mesh in the scenery packages folder, each as a package of its
/// own (<see cref="MeshRegion.PackageName"/>) beside the person's other packages, its overlays in the shared overlays package.
/// <para>
/// Everything is prepared in the hidden <c>.orthostream-download</c> folder there, which is no package (it has no Earth nav data): a
/// zip is unpacked as soon as it is whole, then deleted, and a record of the zips unpacked lets a stopped install go on
/// where it was. The region takes its place only once all of it is there, with its marker written last, so X-Plane never
/// sees half a region. An update is prepared the same way and swapped in at the end.
/// </para>
/// </summary>
internal sealed class RegionInstaller(AssetDownload download, string packagesFolder)
{
    public const string MarkerName = ".orthostreamxp-region.json";
    public const string WorkFolderName = ".orthostream-download";
    /// <summary>Unpacked, a region takes about this much more than its zips (DSFs grow, each small .ter takes a whole disk block).</summary>
    private const double UnpackedRatio = 1.7;
    private const long SpareBytes = 2L << 30;

    private sealed record State(List<string> Unpacked, List<string>? OverlayFiles);

    public string PackagesFolder { get; } = packagesFolder;

    private string WorkRoot => Path.Join(PackagesFolder, WorkFolderName);

    private OverlayPackage Overlays => new(PackagesFolder);

    /// <summary>The regions installed in the packages folder, by id.</summary>
    public IReadOnlyDictionary<string, InstalledRegion> Installed()
    {
        var found = new Dictionary<string, InstalledRegion>();
        foreach (var (id, _, package) in MeshRegion.Known)
        {
            // Still under its old name when it was in use at the start: renamed before anything is installed or deleted.
            string folder = LegacyPackageNames.OldFolderOf(PackagesFolder, package) ?? Path.Join(PackagesFolder, package);
            string marker = Path.Join(folder, MarkerName);
            try
            {
                if (File.Exists(marker) && JsonSerializer.Deserialize<InstalledRegion>(File.ReadAllText(marker)) is { } region)
                    found[id] = region;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Warning($"Could not read {marker}: {ex.Message}");
            }
        }
        return found;
    }

    /// <summary>Bytes of a region already downloaded or unpacked by an install that stopped: what it will not download again.</summary>
    public long AlreadyDone(MeshRegion region, bool withOverlays)
    {
        string work = WorkOf(region);
        State state = LoadState(work);
        long done = region.Mesh.Where(a => state.Unpacked.Contains(a.Name)).Sum(a => a.Size);
        if (withOverlays && state.OverlayFiles is not null) done += region.OverlaysSize;
        foreach (MeshAsset asset in Pending(region, withOverlays, state))
            done += Math.Min(asset.Size, Length(Path.Join(work, "zips", asset.Name)));
        return done;
    }

    /// <summary>Installs or updates <paramref name="region"/>; a stop leaves what was downloaded for the next time.</summary>
    /// <exception cref="InstallException">Not enough disk space, a download that failed, or a damaged zip.</exception>
    public async Task InstallAsync(MeshRegion region, bool withOverlays, IProgress<InstallProgress> progress, CancellationToken cancel)
    {
        CheckRegion(region);
        MoveOldNames();
        string work = WorkOf(region);
        PrepareWork(region, work);
        State state = LoadState(work);
        long total = region.DownloadSize(withOverlays);
        long done = AlreadyDone(region, withOverlays);
        // A zip is downloaded while the one before is unpacked: two can be on the disk at once.
        CheckSpace(total - done, region.Mesh.Count == 0 ? 0 : region.Mesh.Max(a => a.Size));
        Log.Info($"Installing the {region.Name} mesh (release {region.Release}{(withOverlays ? ", with its overlays" : "")}) into {PackagesFolder}" +
                 (done > 0 ? $"; {CacheInventory.FormatBytes(done)} of {CacheInventory.FormatBytes(total)} done before" : ""));
        void Downloaded(long bytes)
        {
            done += bytes;
            progress.Report(new InstallProgress($"Downloading the {region.Name} mesh", done, total));
        }

        // Stopped by the person, or by a zip that cannot be unpacked while the next one downloads.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task unpacking = Task.CompletedTask;
        string unpackingName = "";
        async Task UnpackMeshAsync(string name, string zip)
        {
            try
            {
                await Task.Run(() =>
                {
                    using FileStream file = File.OpenRead(zip);
                    ZipUnpacker.Unpack(file, Path.Join(work, "mesh"), stop.Token);
                }, stop.Token).ConfigureAwait(false);
                // Recorded only once whole: a stop in the middle unpacks the zip again next time, over what is there.
                state.Unpacked.Add(name);
                SaveState(work, state);
                File.Delete(zip);
            }
            catch
            {
                await stop.CancelAsync().ConfigureAwait(false); // no use downloading on: the download alongside stops
                throw;
            }
        }

        try
        {
            try
            {
                // Taken before the loop: the unpack running alongside adds to state.Unpacked.
                MeshAsset[] todo = [.. region.Mesh.Where(a => !state.Unpacked.Contains(a.Name))];
                foreach (MeshAsset asset in todo)
                {
                    string zip = Path.Join(work, "zips", asset.Name);
                    await download.GetAsync(asset, zip, Downloaded, stop.Token).ConfigureAwait(false);
                    // One unpack at a time, in the list's order: the one before ends first.
                    if (!unpacking.IsCompleted) progress.Report(new InstallProgress($"Unpacking {unpackingName}", done, total));
                    await unpacking.ConfigureAwait(false);
                    (unpacking, unpackingName) = (UnpackMeshAsync(asset.Name, zip), asset.Name);
                }

                var parts = new List<string>();
                bool overlays = withOverlays && state.OverlayFiles is null && region.Overlays.Count > 0;
                if (overlays)
                    foreach (MeshAsset part in region.Overlays) // while the last mesh zip is unpacked
                    {
                        string file = Path.Join(work, "zips", part.Name);
                        await download.GetAsync(part, file, Downloaded, stop.Token).ConfigureAwait(false);
                        parts.Add(file);
                    }
                if (!unpacking.IsCompleted) progress.Report(new InstallProgress($"Unpacking {unpackingName}", done, total));
                await unpacking.ConfigureAwait(false);
                if (overlays)
                {
                    progress.Report(new InstallProgress($"Unpacking the {region.Name} overlays", done, total));
                    IReadOnlyList<string> files = await UnpackPartsAsync(parts, Path.Join(work, "overlays"), cancel).ConfigureAwait(false);
                    state = state with { OverlayFiles = [.. files] };
                    SaveState(work, state);
                    foreach (string part in parts) File.Delete(part);
                }
            }
            catch (Exception error)
            {
                // Nothing may write once the install has ended: the unpack running alongside ends first.
                Exception? unpackError = await EndAsync(unpacking).ConfigureAwait(false);
                if (unpackError is not null && error is OperationCanceledException && !cancel.IsCancellationRequested)
                    ExceptionDispatchInfo.Throw(unpackError); // the download was stopped by that failure: it is the cause
                if (unpackError is not null and not OperationCanceledException && !ReferenceEquals(unpackError, error))
                    Log.Warning($"Unpacking {unpackingName} failed as well: {unpackError.Message}");
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
        {
            throw new InstallException($"The {region.Name} mesh could not be installed: {ex.Message}", ex);
        }

        cancel.ThrowIfCancellationRequested(); // a Stop just before the end: nothing is put in place
        progress.Report(new InstallProgress($"Putting the {region.Name} mesh in place", total, total));
        PutInPlace(region, work, withOverlays ? state.OverlayFiles : null);
        Log.Info($"The {region.Name} mesh is installed as {region.PackageName}{(withOverlays ? $", its overlays in {MeshRegion.OverlaysPackageName}" : "")}");
    }

    /// <summary>The downloaded parts of the overlays zip, unpacked as the one zip they make; returns the files, relative.</summary>
    private static Task<IReadOnlyList<string>> UnpackPartsAsync(IReadOnlyList<string> parts, string destination, CancellationToken cancel) =>
        Task.Run(() =>
        {
            using var zip = new ConcatenatedStream(parts);
            return ZipUnpacker.Unpack(zip, destination, cancel);
        }, cancel);

    /// <summary>Waits for a task to end, whatever way it ends; returns its exception, or null.</summary>
    private static async Task<Exception?> EndAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Deletes an installed region and its overlays.</summary>
    /// <exception cref="InstallException">A package still under its old name could not be renamed.</exception>
    public void Delete(string regionId)
    {
        MoveOldNames();
        string package = Path.Join(PackagesFolder, MeshRegion.PackageOf(regionId));
        // The marker first: a region half deleted is no longer taken for installed.
        string marker = Path.Join(package, MarkerName);
        if (File.Exists(marker)) File.Delete(marker);
        if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
        Overlays.Remove(regionId);
        Log.Info($"The {MeshRegion.NameOf(regionId)} mesh was deleted from {PackagesFolder}");
    }

    private void PutInPlace(MeshRegion region, string work, IReadOnlyList<string>? overlayFiles)
    {
        string package = Path.Join(PackagesFolder, region.PackageName);
        string mesh = Path.Join(work, "mesh");
        string old = Path.Join(work, "old");
        if (Directory.Exists(package)) Directory.Move(package, old); // an update: the new release takes its place
        try
        {
            Directory.Move(mesh, package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(old) && !Directory.Exists(package)) Directory.Move(old, package);
            throw new InstallException($"The {region.Name} mesh could not be put in place: {ex.Message}", ex);
        }
        var marker = new InstalledRegion(region.Id, region.Release, overlayFiles is not null, DateTime.UtcNow);
        AtomicFile.Write(Path.Join(package, MarkerName), JsonSerializer.SerializeToUtf8Bytes(marker));

        OverlayPackage overlays = Overlays;
        overlays.Remove(region.Id);
        if (overlayFiles is not null) overlays.Add(region.Id, Path.Join(work, "overlays"), overlayFiles);

        Directory.Delete(work, recursive: true);
        DeleteIfEmpty(Path.GetDirectoryName(work)!);
        DeleteIfEmpty(WorkRoot);
    }

    /// <summary>
    /// Renames what an earlier version installed (<see cref="LegacyPackageNames"/>) first: installed or deleted under the new
    /// name alone, a region would be in the packages folder twice, or not deleted.
    /// </summary>
    private void MoveOldNames()
    {
        IReadOnlyList<string> left = LegacyPackageNames.MovePackages(PackagesFolder);
        if (left.Count == 0) return;
        throw new InstallException($"{string.Join(", ", left)} in {PackagesFolder} could not take its new name ({SceneryPacks.DownloadedPrefix}...). " +
                                   "Close what uses it, such as File Explorer, or remove the folder of the new name if one is there, and try again.");
    }

    private static void DeleteIfEmpty(string folder)
    {
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    /// <summary>The region's work folder; one left by another release of it is deleted (a new release starts afresh).</summary>
    private void PrepareWork(MeshRegion region, string work)
    {
        string regionWork = Path.GetDirectoryName(work)!;
        if (Directory.Exists(regionWork))
            foreach (string other in Directory.EnumerateDirectories(regionWork))
                if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(work), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(other, recursive: true);
        Directory.CreateDirectory(work);
        File.SetAttributes(WorkRoot, File.GetAttributes(WorkRoot) | FileAttributes.Hidden);
    }

    /// <summary>
    /// The region as the list of releases describes it is used for paths and downloads: a known region, plain file names
    /// (never a path that leads elsewhere) and HTTPS addresses only, whether the list came from GitHub or from the copy kept.
    /// </summary>
    private static void CheckRegion(MeshRegion region)
    {
        if (!MeshRegion.IsKnown(region.Id)) throw new InstallException($"'{region.Id}' is not a region OrthoStreamXP knows.");
        foreach (MeshAsset asset in region.Mesh.Concat(region.Overlays))
        {
            bool plainName = asset.Name.Length > 0 && asset.Name == Path.GetFileName(asset.Name)
                             && asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && asset.Name.Trim('.').Length > 0;
            if (!plainName || !IsHttps(asset.Url) || (asset.Sha256Url is { } sha && !IsHttps(sha)))
                throw new InstallException($"The list of the {region.Name} mesh's files has an entry that cannot be used ({asset.Name}). Press Refresh, then try again.");
        }
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <param name="toDownload">What is left to download.</param>
    /// <param name="extraZip">A zip that may be on the disk beside the one being unpacked.</param>
    private void CheckSpace(long toDownload, long extraZip)
    {
        if (SystemCheck.FreeBytes(PackagesFolder) is not { } free) return;
        long needed = (long)(toDownload * UnpackedRatio) + extraZip + SpareBytes;
        if (free < needed)
            throw new InstallException($"Not enough disk space: the install needs about {CacheInventory.FormatBytes(needed)} " +
                                       $"and the drive of {PackagesFolder} has {CacheInventory.FormatBytes(free)} free.");
    }

    private static IEnumerable<MeshAsset> Pending(MeshRegion region, bool withOverlays, State state) =>
        region.Mesh.Where(a => !state.Unpacked.Contains(a.Name))
            .Concat(withOverlays && state.OverlayFiles is null ? region.Overlays : []);

    private string WorkOf(MeshRegion region) => Path.Join(WorkRoot, region.Id, Safe(region.Release));

    private static string Safe(string release) => string.Concat(release.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static State LoadState(string work)
    {
        string file = Path.Join(work, "state.json");
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<State>(File.ReadAllText(file)) ?? new State([], null) : new State([], null);
        }
        catch (JsonException)
        {
            return new State([], null); // unreadable: the zips are unpacked again, over what is there
        }
    }

    private static void SaveState(string work, State state) =>
        AtomicFile.Write(Path.Join(work, "state.json"), JsonSerializer.SerializeToUtf8Bytes(state));

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
