using System.Buffers.Binary;
using System.Security.AccessControl;
using Fsp;
using Microsoft.Win32.SafeHandles;
using OrthoStreamXP.Textures;
using FspFileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Read-only WinFsp file system over a <see cref="SceneryOverlay"/>. Streamed textures are answered by the
/// overlay's <see cref="TextureStreamer"/>; with a <see cref="ReadLog"/>, every open and read is recorded. Photo textures
/// get their colours evened out (<paramref name="colours"/>), then the season's (<paramref name="seasons"/>) and the
/// <paramref name="exposure"/> as they are read (<see cref="PhotoColours"/>).
/// </summary>
/// <param name="package">The package name in the read log.</param>
/// <param name="loads">Notes the scenery tiles (DSFs) X-Plane loads.</param>
/// <param name="exposure">The exposure's lookup table (<see cref="Exposure"/>), or null for the photos as they are.</param>
internal sealed class SceneryFileSystem(SceneryOverlay overlay, string label, ReadLog? readLog = null, string package = "", SeasonClock? seasons = null,
    OrthoStreamXP.Setup.AreaUsage? usage = null, ColourMatcher? colours = null, SceneryLoads? loads = null, ushort[]? exposure = null)
    : FileSystemBase
{
    private const ulong TotalSize = 1UL << 40, FreeSize = 1UL << 39;

    private static readonly byte[] Security = SecurityFromSddl("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)");
    private static readonly ulong MountedAt = (ulong)DateTime.UtcNow.ToFileTimeUtc();

    private sealed class Handle(VirtualPath path, string backing, bool isDirectory)
    {
        public VirtualPath Path { get; } = path;
        public string Backing { get; } = backing;
        public bool IsDirectory { get; } = isDirectory;
        public SafeFileHandle? File { get; init; }
        /// <summary>Fixed while open: replacements are renamed over the path, never written in place.</summary>
        public long Length { get; init; }
        public FileSystemInfo[]? Entries { get; set; }
        /// <summary>A texture served level by level (no backing file), and the levels this handle was given.</summary>
        public StreamedTexture? Streamed { get; init; }
        /// <summary>A terrain definition made in memory (no backing file).</summary>
        public byte[]? Content { get; init; }
        /// <summary>A night texture painted as X-Plane reads it (no backing file).</summary>
        public NightTexture? Night { get; init; }
        /// <summary>A photo texture X-Plane gets as a finished file: its reads are still paging evidence.</summary>
        public StreamedTexture? Photo { get; init; }
        public ReadCursor? Cursor { get; init; }
        /// <summary>A photo texture whose colours change as it is read: its name, colour-block size (8 or 16) and width; 0 bytes: left as it is.</summary>
        public TextureName SeasonName { get; init; }
        public int SeasonBlockBytes { get; init; }
        public int SeasonSize { get; init; }
        /// <summary>The backing file's write time: a new one is measured again for its colours.</summary>
        public DateTime FileVersion { get; init; }
    }

    private static byte[] SecurityFromSddl(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        byte[] bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = 512;
        host.SectorsPerAllocationUnit = 1;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = 0;
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = true;
        // A streamed texture's bytes improve while its size stays the same, so X-Plane must never get old cached
        // pages. With FileInfoTimeout below INFINITE, WinFsp does not cache file data (every read reaches Read at
        // X-Plane's own offsets); should that change, dropping the pages when the last handle closes still keeps
        // each load fresh.
        host.FlushAndPurgeOnCleanup = overlay.Streamer is not null;
        host.FileSystemName = "OrthoStreamXP";
        return STATUS_SUCCESS;
    }

    public override int ExceptionHandler(Exception ex)
    {
        switch (ex)
        {
            case NtStatusException nt:
                return nt.Status;
            case FileNotFoundException or DirectoryNotFoundException:
                return STATUS_OBJECT_NAME_NOT_FOUND;
            case UnauthorizedAccessException:
                return STATUS_ACCESS_DENIED;
            default:
                Log.Error("File system operation failed", ex);
                return base.ExceptionHandler(ex);
        }
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = TotalSize;
        volumeInfo.FreeSize = FreeSize;
        volumeInfo.SetVolumeLabel(label);
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        OverlayEntry entry = overlay.Open(fileName);
        fileAttributes = (uint)(entry.Backing is { } backing && Directory.Exists(backing) ? FileAttributes.Directory : FileAttributes.Archive);
        if (securityDescriptor is not null) securityDescriptor = Security;
        return STATUS_SUCCESS;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = Security;
        return STATUS_SUCCESS;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FspFileInfo fileInfo, out string normalizedName)
    {
        var path = VirtualPath.Parse(fileName);
        OverlayEntry entry = overlay.Open(path);
        fileDesc = null!;
        normalizedName = null!;
        if (entry.Streamed is { } streamed)
        {
            usage?.Note(OrthoStreamXP.Setup.AreaName.Of(streamed.Name));
            fileNode = new Handle(path, "", false)
            {
                Streamed = streamed, Cursor = new ReadCursor(), Length = streamed.Layout.Length,
                SeasonName = streamed.Name, SeasonBlockBytes = Recolours ? 8 : 0, SeasonSize = streamed.Layout.Size,
            };
            fileInfo = Describe(streamed);
            readLog?.Open(package, path, "streamed");
            return STATUS_SUCCESS;
        }
        if (entry.Content is { } content)
        {
            fileNode = new Handle(path, "", false) { Content = content, Length = content.Length };
            fileInfo = Describe(content);
            readLog?.Open(package, path, "memory");
            return STATUS_SUCCESS;
        }
        if (entry.Night is { } night)
        {
            fileNode = new Handle(path, "", false) { Night = night, Cursor = new ReadCursor(), Length = night.Layout.Length };
            fileInfo = Describe(night);
            readLog?.Open(package, path, "night");
            return STATUS_SUCCESS;
        }
        string backing = entry.Backing!;
        bool isDirectory = Directory.Exists(backing);
        // Share delete so a finished build can atomically replace a DDS that X-Plane still has open.
        SafeFileHandle? file = isDirectory ? null : File.OpenHandle(backing, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = file is null ? 0 : RandomAccess.GetLength(file);
        // A finished photo texture (the cache's complete DDS, laid out as streamed ones are) keeps its read history.
        StreamedTexture? photo = file is not null && overlay.Streamer?.Get(path) is { } t && t.Layout.Length == length ? t : null;
        var (seasonName, blockBytes, size) = SeasonTexture(path, file);
        if (usage is not null && !isDirectory && path.IsDds && TextureName.TryParseFile(path.Name, out TextureName used)) usage.Note(OrthoStreamXP.Setup.AreaName.Of(used));
        var handle = new Handle(path, backing, isDirectory)
        {
            File = file,
            Length = length,
            Photo = photo,
            Cursor = photo is null ? null : new ReadCursor(),
            SeasonName = seasonName,
            SeasonBlockBytes = blockBytes,
            SeasonSize = size,
            FileVersion = blockBytes > 0 ? System.IO.File.GetLastWriteTimeUtc(backing) : default,
        };
        fileNode = handle;
        fileInfo = Describe(isDirectory ? new DirectoryInfo(backing) : new FileInfo(backing));
        if (!isDirectory)
        {
            readLog?.Open(package, path, "file");
            loads?.Opened(path, package);
        }
        return STATUS_SUCCESS;
    }

    public override void Close(object fileNode, object fileDesc) => ((Handle)fileNode).File?.Dispose();

    public override int GetFileInfo(object fileNode, object fileDesc, out FspFileInfo fileInfo)
    {
        var handle = (Handle)fileNode;
        fileInfo = handle.Streamed is { } streamed ? Describe(streamed)
            : handle.Content is { } content ? Describe(content)
            : handle.Night is { } night ? Describe(night)
            : Describe(handle.IsDirectory ? new DirectoryInfo(handle.Backing) : new FileInfo(handle.Backing));
        return STATUS_SUCCESS;
    }

    public override int Flush(object fileNode, object fileDesc, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        return fileNode is null ? STATUS_SUCCESS : GetFileInfo(fileNode, fileDesc, out fileInfo);
    }

    public override unsafe int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var handle = (Handle)fileNode;
        if (offset >= (ulong)handle.Length) return handle.File is null && handle.Streamed is null && handle.Content is null && handle.Night is null ? STATUS_ACCESS_DENIED : STATUS_END_OF_FILE;
        var target = new Span<byte>((void*)buffer, checked((int)length));
        // Before the read: a photo arriving meanwhile is read with the base's correction until the next read, never the reverse.
        TextureColour? colour = ColourOf(handle);
        if (handle.Streamed is { } streamed)
        {
            ReadAnswer answer = overlay.Streamer!.Read(streamed, handle.Cursor!, (long)offset, target);
            bytesTransferred = (uint)answer.Bytes;
            Recolor(handle, target[..answer.Bytes], offset, colour);
            LogTextureRead(handle.Path, (long)offset, answer, streamed);
            return STATUS_SUCCESS;
        }
        if (handle.Content is { } content)
        {
            int count = Math.Min(target.Length, content.Length - (int)offset);
            content.AsSpan((int)offset, count).CopyTo(target);
            bytesTransferred = (uint)count;
            readLog?.Read(new ReadRecord(package, handle.Path, (long)offset, count));
            return STATUS_SUCCESS;
        }
        if (handle.Night is { } night)
        {
            int count = night.Read(handle.Cursor!, (long)offset, target);
            bytesTransferred = (uint)count;
            readLog?.Read(new ReadRecord(package, handle.Path, (long)offset, count));
            return STATUS_SUCCESS;
        }
        if (handle.File is null) return STATUS_ACCESS_DENIED;
        bytesTransferred = (uint)RandomAccess.Read(handle.File, target, (long)offset);
        Recolor(handle, target[..(int)bytesTransferred], offset, colour);
        if (handle.Photo is { } photo)
            LogTextureRead(handle.Path, (long)offset, overlay.Streamer!.NoteFileRead(photo, handle.Cursor!, (long)offset, (int)bytesTransferred), photo);
        else
            readLog?.Read(new ReadRecord(package, handle.Path, (long)offset, (int)bytesTransferred));
        return STATUS_SUCCESS;
    }

    /// <summary>Whether photo textures get any colour adjustment.</summary>
    private bool Recolours => seasons is not null || colours is not null || exposure is not null;

    /// <summary>The evened-out colours, the season's and the exposure for a photo texture's bytes (the cache keeps the imagery as downloaded).</summary>
    private void Recolor(Handle handle, Span<byte> data, ulong offset, TextureColour? colour)
    {
        if (handle.SeasonBlockBytes == 0) return;
        PhotoColours.Apply(data, (long)offset, handle.SeasonBlockBytes, handle.SeasonSize, colour, seasons?.TableFor(handle.SeasonName), exposure);
    }

    /// <summary>
    /// A photo texture's colour correction: measured on the photo (a streamed texture's real data, or the file), or the
    /// base's while the picture is still the low-zoom base; null when there is none.
    /// </summary>
    private TextureColour? ColourOf(Handle handle)
    {
        if (colours is null || handle.SeasonBlockBytes == 0) return null;
        if (handle.Streamed is { } streamed)
        {
            TextureStreamer streamer = overlay.Streamer!;
            var (source, version) = streamer.Picture(streamed);
            return source == PictureSource.Photo
                ? colours.ForPhoto(streamed.Name, version, size => streamer.PhotoSample(streamed, size))
                : colours.ForBase(streamed.Name);
        }
        return handle.SeasonBlockBytes == 8 && handle.File is { } file
            ? colours.ForPhoto(handle.SeasonName, handle.FileVersion, size => FileSample(file, handle.SeasonSize, size))
            : null;
    }

    /// <summary>The level of a DXT1 file nearest <paramref name="size"/> pixels, decoded to RGB; null when the file is not a full chain of levels.</summary>
    internal static (byte[] Rgb, int Size)? FileSample(SafeFileHandle file, int fileSize, int size)
    {
        var layout = DdsLayout.For(fileSize);
        if (RandomAccess.GetLength(file) < layout.Length) return null;
        int mip = Math.Clamp((int)Math.Round(Math.Log2((double)fileSize / size)), 0, layout.Levels - 1), levelSize = layout.SizeOf(mip);
        byte[] blocks = new byte[layout.LengthOf(mip)];
        return RandomAccess.Read(file, blocks, layout.Offset(mip)) < blocks.Length ? null : (Dds.DecodeLevel(blocks, levelSize, levelSize), levelSize);
    }

    /// <summary>A photo texture file whose colours can change: square DXT1, DXT3 or DXT5, named like 21904_35824_BI16.dds.</summary>
    private (TextureName Name, int BlockBytes, int Size) SeasonTexture(VirtualPath path, SafeFileHandle? file)
    {
        if (!Recolours || file is null || !path.IsDds || !TextureName.TryParseFile(path.Name, out TextureName name)) return default;
        Span<byte> header = stackalloc byte[Dds.HeaderSize];
        if (RandomAccess.Read(file, header, 0) < Dds.HeaderSize || !header[..4].SequenceEqual("DDS "u8)) return default;
        int height = BinaryPrimitives.ReadInt32LittleEndian(header[12..]), width = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        int blockBytes = ColourTable.BlockBytes(header.Slice(84, 4));
        return width == height && width > 0 && blockBytes > 0 ? (name, blockBytes, width) : default;
    }

    private void LogTextureRead(VirtualPath path, long offset, ReadAnswer answer, StreamedTexture texture) =>
        readLog?.Read(new ReadRecord(package, path, offset, answer.Bytes, answer.FirstMip, answer.LastMip,
            answer.Band?.ToString().ToLowerInvariant(), answer.Served?.ToString().ToLowerInvariant(), answer.WaitMs, answer.DistanceM,
            texture.SizeM, texture.Layout.Size, answer.Wanted));

    private int _rootListed;

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FspFileInfo fileInfo)
    {
        var handle = (Handle)fileNode;
        if (handle.Entries is null)
        {
            var self = new DirectoryInfo(handle.Backing);
            IEnumerable<FileSystemInfo> children = overlay.List(handle.Path);
            if (handle.Path.IsRoot && Interlocked.Exchange(ref _rootListed, 1) == 0)
                Log.Info($"{package}: the package folder was listed for the first time (by X-Plane or another program)");
            // "." and ".." are named by index below; the infos are only used for their attributes and times.
            handle.Entries = handle.Path.IsRoot ? [.. children] : [self, self, .. children];
        }
        int dots = handle.Path.IsRoot ? 0 : 2;
        int index;
        if (context is null)
        {
            index = 0;
            if (marker is not null)
                while (index < handle.Entries.Length && string.Compare(EntryName(index, dots, handle.Entries), marker, StringComparison.OrdinalIgnoreCase) <= 0)
                    index++;
        }
        else
        {
            index = (int)context;
        }
        if (index < handle.Entries.Length)
        {
            context = index + 1;
            fileName = EntryName(index, dots, handle.Entries);
            // A tile's terrain definition is listed as it reads (the package's file, if any, has other sizes).
            fileInfo = index >= dots && handle.Entries[index] is FileInfo && overlay.TerrainDefinition(handle.Path.Child(fileName)) is { } definition
                ? Describe(definition)
                : Describe(handle.Entries[index]);
            return true;
        }
        fileName = null!;
        fileInfo = default;
        return false;
    }

    private static string EntryName(int index, int dots, FileSystemInfo[] entries) =>
        index < dots ? (index == 0 ? "." : "..") : entries[index].Name;

    public override int GetDirInfoByName(object fileNode, object fileDesc, string fileName, out string normalizedName, out FspFileInfo fileInfo)
    {
        var handle = (Handle)fileNode;
        OverlayEntry entry = overlay.Open(handle.Path.Child(fileName));
        normalizedName = fileName;
        fileInfo = entry.Streamed is { } streamed ? Describe(streamed)
            : entry.Content is { } content ? Describe(content)
            : entry.Night is { } night ? Describe(night)
            : Directory.Exists(entry.Backing) ? Describe(new DirectoryInfo(entry.Backing!)) : Describe(new FileInfo(entry.Backing!));
        return STATUS_SUCCESS;
    }

    /// <summary>A streamed texture: its full size, and the time of its latest real data as the write time.</summary>
    private static FspFileInfo Describe(StreamedTexture texture)
    {
        ulong size = (ulong)texture.Layout.Length, written = (ulong)TextureStreamer.VersionOf(texture).ToFileTimeUtc();
        return new FspFileInfo
        {
            FileAttributes = (uint)FileAttributes.Archive,
            AllocationSize = size,
            FileSize = size,
            CreationTime = MountedAt,
            LastAccessTime = written,
            LastWriteTime = written,
            ChangeTime = written,
        };
    }

    /// <summary>A terrain definition made in memory: its size, and the mount time.</summary>
    private static FspFileInfo Describe(byte[] content) => Describe((ulong)content.Length);

    /// <summary>A night texture: its full size, every level, whatever is painted yet; the mount time.</summary>
    private static FspFileInfo Describe(NightTexture night) => Describe((ulong)night.Layout.Length);

    private static FspFileInfo Describe(ulong size) => new()
    {
        FileAttributes = (uint)FileAttributes.Archive,
        AllocationSize = size,
        FileSize = size,
        CreationTime = MountedAt,
        LastAccessTime = MountedAt,
        LastWriteTime = MountedAt,
        ChangeTime = MountedAt,
    };

    private static FspFileInfo Describe(FileSystemInfo item)
    {
        bool isDirectory = item is DirectoryInfo;
        ulong size = isDirectory ? 0 : (ulong)((FileInfo)item).Length;
        ulong written = (ulong)item.LastWriteTimeUtc.ToFileTimeUtc();
        return new FspFileInfo
        {
            FileAttributes = (uint)(isDirectory ? FileAttributes.Directory : FileAttributes.Archive),
            AllocationSize = size,
            FileSize = size,
            CreationTime = (ulong)item.CreationTimeUtc.ToFileTimeUtc(),
            LastAccessTime = (ulong)item.LastAccessTimeUtc.ToFileTimeUtc(),
            LastWriteTime = written,
            ChangeTime = written,
        };
    }

    // Scenery is read-only: reject writes and metadata changes rather than letting them reach the source package.

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor,
        ulong allocationSize, out object fileNode, out object fileDesc, out FspFileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo,
        out uint bytesTransferred, out FspFileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FspFileInfo fileInfo)
    {
        fileInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) => STATUS_MEDIA_WRITE_PROTECTED;

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) =>
        STATUS_MEDIA_WRITE_PROTECTED;

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor) =>
        STATUS_MEDIA_WRITE_PROTECTED;
}
