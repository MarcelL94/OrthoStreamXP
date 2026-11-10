using System.IO.Compression;

namespace OrthoStreamXP.Setup;

/// <summary>
/// Unpacks a region's zip into a folder. The zips hold their package under a folder of their own (z_eur_03/Earth nav data/...,
/// and the overlays one or two folders deeper), which is taken off: the files land where the package keeps them. Each file
/// is written beside its place and renamed over it, so an interrupted unpack leaves no half file; an entry that would land
/// outside the folder is refused.
/// </summary>
internal static class ZipUnpacker
{
    private const string EarthNavData = "Earth nav data/";

    /// <summary>Unpacks the files under the zip's package folder (the folder that holds Earth nav data); returns them, relative.</summary>
    /// <exception cref="InvalidDataException">The zip holds no package, or an entry points outside the folder.</exception>
    public static IReadOnlyList<string> Unpack(Stream zip, string destination, CancellationToken cancel)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        string prefix = PackagePrefix(archive.Entries.Select(e => e.FullName.Replace('\\', '/')))
                        ?? throw new InvalidDataException("The zip holds no scenery package (no Earth nav data folder)");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        var written = new List<string>();
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancel.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith('/')) continue;
            string relative = name[prefix.Length..];
            // A drive or an NTFS stream (a colon), or characters no path may hold: never a file of the package.
            if (relative.Contains(':', StringComparison.Ordinal) || relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new InvalidDataException($"The zip entry {entry.FullName} is not a usable file name");
            string path;
            try
            {
                path = Path.GetFullPath(Path.Join(root, relative));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException($"The zip entry {entry.FullName} is not a usable file name", ex);
            }
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The zip entry {entry.FullName} points outside the package");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string part = path + ".part";
            entry.ExtractToFile(part, overwrite: true);
            File.Move(part, path, overwrite: true);
            written.Add(relative.Replace('/', Path.DirectorySeparatorChar));
        }
        return written;
    }

    /// <summary>The path in the zip of the folder holding Earth nav data ("z_eur_03/", "" at the top), or null when there is none.</summary>
    internal static string? PackagePrefix(IEnumerable<string> entries)
    {
        foreach (string name in entries)
        {
            if (name.StartsWith(EarthNavData, StringComparison.Ordinal)) return "";
            int at = name.IndexOf("/" + EarthNavData, StringComparison.Ordinal);
            if (at >= 0) return name[..(at + 1)];
        }
        return null;
    }
}

/// <summary>Files read one after another as one seekable stream: the parts of a zip cut in pieces (y_eur_overlays.zip.00, .01...).</summary>
internal sealed class ConcatenatedStream : Stream
{
    private readonly FileStream[] _parts;
    private readonly long[] _starts;
    private long _position;

    public ConcatenatedStream(IReadOnlyList<string> files)
    {
        _parts = new FileStream[files.Count];
        _starts = new long[files.Count + 1];
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                _parts[i] = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                _starts[i + 1] = _starts[i] + _parts[i].Length;
            }
        }
        catch
        {
            Dispose(true);
            throw;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _starts[^1];

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length || buffer.IsEmpty) return 0;
        int part = Array.BinarySearch(_starts, _position);
        part = part >= 0 ? part : ~part - 1;
        while (part < _parts.Length - 1 && _starts[part + 1] <= _position) part++;
        FileStream file = _parts[part];
        file.Position = _position - _starts[part];
        int read = file.Read(buffer[..(int)Math.Min(buffer.Length, _starts[part + 1] - _position)]);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (FileStream? part in _parts)
                part?.Dispose();
        base.Dispose(disposing);
    }
}
