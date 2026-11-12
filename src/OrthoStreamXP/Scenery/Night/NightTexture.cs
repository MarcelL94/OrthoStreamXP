using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// One photo tile's night texture as X-Plane reads it: always its full size, header and every level, but painted only
/// when X-Plane reads a level, and then only from the finest level it wants (<see cref="NightStore"/>). Its header
/// chunk gets the start of level 0 kept for it; a level not painted reads as black.
/// </summary>
internal sealed class NightTexture
{
    private readonly NightTextures _owner;
    private readonly Lock _gate = new();
    /// <summary>The finest level kept in the cache file; <see cref="DdsLayout.Levels"/> when nothing is; -1 until looked up.</summary>
    private int _from = -1;
    private bool _dark;

    /// <param name="side">The texture's side: its photo's (X-Plane reads both at the same level number).</param>
    public NightTexture(NightTextures owner, TextureName name, int side, string file)
    {
        _owner = owner;
        Name = name;
        File = file;
        Layout = DdsLayout.For(side);
    }

    public TextureName Name { get; }
    public DdsLayout Layout { get; }
    /// <summary>The cache file.</summary>
    public string File { get; }

    /// <summary>
    /// Answers one of X-Plane's reads: the header, and the levels it covers. A read of a level not kept yet paints the
    /// texture from that level first; X-Plane's header chunk paints it only the first time (with the levels it reads
    /// far away), and its start of level 0 is kept with them. Returns the bytes read.
    /// </summary>
    public int Read(ReadCursor cursor, long offset, Span<byte> target)
    {
        long end = Math.Min(Layout.Length, offset + target.Length);
        if (offset >= end) return 0;
        int count = (int)(end - offset);
        Span<byte> output = target[..count];
        if (offset < Dds.HeaderSize) Layout.Header[(int)offset..(int)Math.Min(end, Dds.HeaderSize)].CopyTo(output);
        if (end <= Dds.HeaderSize) return count;

        bool headerChunk = ReadPattern.IsHeaderChunk(Layout, offset, end);
        long dataStart = Math.Max(offset, Dds.HeaderSize);
        var (wanted, _) = ReadPattern.WantedLevel(Layout, cursor, dataStart, end, headerChunk);
        ReadPattern.NoteRead(cursor, end, headerChunk);
        if (wanted is int mip) Ensure(mip);
        else if (headerChunk) Ensure(Layout.Levels - 1, firstLook: true);

        output[(int)(dataStart - offset)..].Clear();
        int from = Fill(output, offset, dataStart, end);
        if (!headerChunk)
            for (int m = Layout.MipAt(dataStart); m <= Layout.MipAt(end - 1); m++)
                if (m >= from) cursor.Set(m, ReadOnlyMemory<byte>.Empty, Served.Real);
        return count;
    }

    /// <summary>
    /// Makes sure level <paramref name="mip"/> and the smaller ones are kept. <paramref name="firstLook"/>: only if
    /// nothing is kept yet, and then from the level X-Plane reads far away.
    /// </summary>
    private void Ensure(int mip, bool firstLook = false)
    {
        lock (_gate)
        {
            if (_from < 0) _from = Probe();
            if (_dark || (firstLook ? _from < Layout.Levels : _from <= NightStore.Normalize(Layout, mip))) return;
            int from = NightStore.Normalize(Layout, firstLook ? NightTextures.FarLevel(Layout) : mip);
            switch (_owner.Paint(this, from))
            {
                case true:
                    _from = from;
                    break;
                case false:
                    _dark = true; // no light here at all: black, and nothing kept
                    break;
                case null:
                    break; // could not be kept: black for this read, painted again at the next
            }
        }
    }

    /// <summary>The finest level the cache file holds; <see cref="DdsLayout.Levels"/> for none.</summary>
    private int Probe()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return Layout.Levels;
            using (var handle = System.IO.File.OpenHandle(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                if (Kept(handle) is { } contents) return contents.From;
            System.IO.File.Delete(File); // not a night texture of this size (the photos' size changed): painted again
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"The night texture of {Name.Stem} could not be looked up ({ex.Message}); it is painted again");
        }
        return Layout.Levels;
    }

    /// <summary>What the cache file holds, when it is a night texture of this size.</summary>
    private NightFile.Contents? Kept(Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
        NightFile.ReadContents(handle) is { } contents && NightStore.Normalize(Layout, contents.From) == contents.From
            && contents.Length == NightStore.Length(Layout, contents.From) ? contents : null;

    /// <summary>Copies what the cache file keeps of the read's range into <paramref name="output"/>; returns the level it starts at.</summary>
    private int Fill(Span<byte> output, long offset, long dataStart, long end)
    {
        lock (_gate)
            if (_dark || _from >= Layout.Levels) return Layout.Levels;
        try
        {
            using var handle = System.IO.File.OpenHandle(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The file as it is now: it may have been widened since this read was decided.
            if (Kept(handle) is not { } contents) return Layout.Levels;
            int from = contents.From;
            // At most two pieces are kept: the header chunk's part of level 0, and the levels from "from" (one piece
            // when the whole texture is).
            (long Start, long End)[] pieces = from == 0
                ? [(Dds.HeaderSize, Layout.Length)]
                : [(Dds.HeaderSize, NightStore.HeadEnd(Layout)), (Layout.Offset(from), Layout.Length)];
            foreach (var (pieceStart, pieceEnd) in pieces)
            {
                long a = Math.Max(dataStart, pieceStart), b = Math.Min(end, pieceEnd);
                if (a >= b) continue;
                NightFile.Read(handle, contents, NightStore.Position(Layout, from, a), output.Slice((int)(a - offset), (int)(b - a)));
            }
            return from;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Layout.Levels; // removed with its area meanwhile: black
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning($"The night texture of {Name.Stem} could not be read ({ex.Message}); it is black for this read");
            return Layout.Levels;
        }
    }
}
