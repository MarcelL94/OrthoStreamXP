using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>The NTSTATUS codes path resolution raises (the file system uses WinFsp's own constants).</summary>
internal static class NtStatus
{
    public const int AccessDenied = unchecked((int)0xC0000022);
    public const int ObjectNameNotFound = unchecked((int)0xC0000034);
}

/// <summary>An NTSTATUS result raised from deep inside path resolution and returned to WinFsp.</summary>
internal sealed class NtStatusException(int status) : Exception($"NTSTATUS 0x{status:X8}")
{
    public int Status { get; } = status;
}

/// <summary>A path inside the mounted package, split into validated components.</summary>
internal sealed class VirtualPath
{
    private VirtualPath(string[] parts)
    {
        Parts = parts;
        Key = string.Join('\\', parts).ToUpperInvariant();
    }

    public string[] Parts { get; }
    public string Name => Parts.Length == 0 ? "" : Parts[^1];
    /// <summary>Case-insensitive identity used for locks, the background queue and memoized lookups.</summary>
    public string Key { get; }
    public string Display => string.Join('/', Parts);
    public bool IsRoot => Parts.Length == 0;

    /// <summary>Splits a Win32 path from WinFsp ("\textures\a.dds") and rejects traversal or stream names.</summary>
    public static VirtualPath Parse(string fileName)
    {
        string[] parts = fileName.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
            if (part is "." or ".." || part.Contains(':')) throw new NtStatusException(NtStatus.AccessDenied);
        return new VirtualPath(parts);
    }

    public static VirtualPath Of(params string[] parts) => new(parts);

    public VirtualPath Child(string name) => new([.. Parts, name]);

    /// <summary>True for a file directly or indirectly inside the top-level folder <paramref name="top"/>.</summary>
    public bool IsUnder(string top) => Parts.Length >= 2 && Parts[0].Equals(top, StringComparison.OrdinalIgnoreCase);

    public bool HasExtension(string extension) => Path.GetExtension(Name).Equals(extension, StringComparison.OrdinalIgnoreCase);

    public bool IsDds => IsUnder("textures") && HasExtension(".dds");

    public bool IsTerrain => IsUnder("terrain") && HasExtension(".ter");

    public VirtualPath WithExtension(string extension) => new([.. Parts[..^1], Path.ChangeExtension(Name, extension)]);

    public bool Is(params string[] parts) =>
        Parts.Length == parts.Length && Parts.Zip(parts).All(p => p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Display;
}

/// <summary>The three folders behind the mount: the source package, the cache overlay and an optional DDS store.</summary>
internal sealed class SceneryRoots
{
    public SceneryRoots(string source, string cache, string? textureStore, TexturePlan textures)
    {
        Source = Path.GetFullPath(source);
        Cache = Path.GetFullPath(cache);
        TextureStore = textureStore is null ? null : Path.GetFullPath(textureStore);
        Textures = textures;
    }

    public string Source { get; }
    public string Cache { get; }
    public string? TextureStore { get; }
    /// <summary>How textures are built here: their zoom and size depend on their own file zoom.</summary>
    public TexturePlan Textures { get; }

    /// <summary>
    /// Source folders that together appear as the virtual textures folder, in priority order. Some packages
    /// keep everything in textures\; others ship their PNG masks in _textures\, which is meant to appear as textures.
    /// </summary>
    public static readonly string[] SourceTextureFolders = ["textures", "_textures"];

    /// <summary>The existing file or directory at <paramref name="parts"/> under <paramref name="root"/>, or null.</summary>
    public static string? Find(string root, IEnumerable<string> parts)
    {
        string candidate = Path.Join(root, string.Join('\\', parts));
        return File.Exists(candidate) || Directory.Exists(candidate) ? candidate : null;
    }

    private static bool IsTexturesTree(VirtualPath path) =>
        path.Parts.Length >= 1 && path.Parts[0].Equals("textures", StringComparison.OrdinalIgnoreCase);

    /// <summary>The source package's real folders behind a virtual directory (several for the textures tree).</summary>
    public IEnumerable<string> SourceFolders(VirtualPath path) =>
        IsTexturesTree(path)
            ? SourceTextureFolders.Select(folder => Path.Join(Source, folder, string.Join('\\', path.Parts[1..])))
            : [Path.Join(Source, string.Join('\\', path.Parts))];

    /// <summary>
    /// The existing source file or directory behind a virtual path, looking in textures\ then _textures\.
    /// The package's own photo textures are never found (see <see cref="IsPackagePhoto"/>).
    /// </summary>
    public string? FindInSource(VirtualPath path) =>
        IsPackagePhoto(path) ? null
        : SourceFolders(path).FirstOrDefault(candidate => File.Exists(candidate) || Directory.Exists(candidate));

    /// <summary>
    /// Whether a path names a photo texture (textures\21904_35824_BI16.dds). The package's own copies of these
    /// (made by Ortho4XP or similar) are hidden and never served: photos are always streamed or downloaded,
    /// while the package's masks (PNGs) and other files pass through as they are.
    /// </summary>
    public static bool IsPackagePhoto(VirtualPath path) => PhotoTexture(path) is not null;

    /// <summary>
    /// Where a virtual path is kept in the cache: a photo texture under its cache name (textures\87520_143440_BI18.dds
    /// → textures\43760_71720_PX17.dds at level -1, see <see cref="TexturePlan.CacheStem"/>), anything else as it is named.
    /// </summary>
    public string CachePath(VirtualPath path) =>
        PhotoTexture(path) is { } name
            ? Path.Join(Cache, "textures", Textures.CacheStem(name) + ".dds")
            : Path.Join(Cache, string.Join('\\', path.Parts));

    /// <summary>The level chain file of a photo texture: .xpstream\mips\43760_71720_PX17.mip2.</summary>
    public string ChainPath(TextureName name, int level) =>
        Path.Join(Cache, ".xpstream", "mips", FormattableString.Invariant($"{Textures.CacheStem(name)}.mip{level}"));

    private static TextureName? PhotoTexture(VirtualPath path) =>
        path.Parts.Length == 2 && path.IsDds && TextureName.TryParseFile(path.Name, out TextureName name) ? name : null;

    public string ProvisionalPath(VirtualPath path) => Path.Join(Cache, ".xpstream", "provisional", string.Join('\\', path.Parts));

    /// <summary>The existing cached file or directory behind a virtual path, or null.</summary>
    public string? FindCached(VirtualPath path)
    {
        string candidate = CachePath(path);
        return File.Exists(candidate) || Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>A usable cached file: non-DDS files always, a photo texture only at the size it is built at now.</summary>
    public string? CachedFile(VirtualPath path)
    {
        string? cached = FindCached(path);
        return cached is not null && File.Exists(cached) && (!path.HasExtension(".dds") || DdsMatchesSize(path, cached)) ? cached : null;
    }

    /// <summary>
    /// Renames the photo textures and level chains earlier versions kept under the package's names to their cache names
    /// (<see cref="TexturePlan.CacheStem"/>), so nothing is downloaded again. Where two package names (different codes)
    /// share a cache name, the first is kept: the code never chose the imagery.
    /// </summary>
    public void RenameToCacheNames()
    {
        int renamed = 0;
        foreach (var (folder, pattern) in new[] { (Path.Join(Cache, "textures"), "*.dds"), (Path.Join(Cache, ".xpstream", "mips"), "*.mip*") })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder, pattern))
            {
                string fileName = Path.GetFileName(file);
                int dot = fileName.IndexOf('.', StringComparison.Ordinal);
                if (!TextureName.TryParseStem(fileName[..dot], out TextureName name) || Textures.CacheStem(name) == name.Stem) continue;
                string destination = Path.Join(folder, Textures.CacheStem(name) + fileName[dot..]);
                try
                {
                    if (File.Exists(destination)) File.Delete(file);
                    else File.Move(file, destination);
                    renamed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // left for the next start
                }
            }
        }
        if (renamed > 0) Log.Info($"Renamed {renamed} cached textures in {Cache} to the zoom they were downloaded at");
    }

    /// <summary>Whether <paramref name="file"/> has the size <paramref name="path"/>'s texture is built at (any valid DDS for other names).</summary>
    public bool DdsMatchesSize(VirtualPath path, string file)
    {
        var dimensions = Dds.ReadDimensions(file);
        return Textures.SizeFor(path.Name) is { } size ? dimensions == (size, size) : dimensions is not null;
    }

    /// <summary>
    /// A file under textures/ that the package itself provides: from the source's textures\ or _textures\
    /// (never a photo texture, see <see cref="IsPackagePhoto"/>), or from the lazy texture store (which holds a
    /// textures folder's contents). For a store DDS this is the local copy that can be served while, or instead
    /// of, a download.
    /// </summary>
    public string? FindTexture(VirtualPath path)
    {
        if (!path.IsUnder("textures")) return null;
        if (FindInSource(path) is { } source && File.Exists(source)) return source;
        return TextureStore is not null && Find(TextureStore, path.Parts.Skip(1)) is { } stored && File.Exists(stored) ? stored : null;
    }
}
