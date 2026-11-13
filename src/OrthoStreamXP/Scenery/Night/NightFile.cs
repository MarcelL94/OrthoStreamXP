using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// A night texture's cache file: what is kept of it (<see cref="NightStore"/>) in 64 KB chunks, each compressed on its
/// own, so a read unpacks only the chunks it covers. A night texture is mostly black: an all-black chunk takes no
/// space at all, and the others shrink to a fraction.
/// <para>Layout: "NLT3", the level the kept part starts at (int), its length (long), the chunk count (int), each
/// chunk's compressed length (int, 0 for black), then the chunks.</para>
/// </summary>
internal static class NightFile
{
    private const int Chunk = 65536;
    private static ReadOnlySpan<byte> Magic => "NLT3"u8;

    /// <summary>The level the kept part starts at, its length, and where each chunk is in the file (0 length: black).</summary>
    internal sealed record Contents(int From, long Length, long[] ChunkStart, int[] ChunkLength);

    /// <summary>Writes the kept part <paramref name="kept"/> starting at level <paramref name="from"/>, whole or not at all.</summary>
    public static void Write(string path, int from, byte[] kept)
    {
        int chunks = (int)((kept.LongLength + Chunk - 1) / Chunk);
        var bodies = new byte[chunks][];
        for (int i = 0; i < chunks; i++)
        {
            ReadOnlySpan<byte> piece = kept.AsSpan(i * Chunk, (int)Math.Min(Chunk, kept.LongLength - (long)i * Chunk));
            if (piece.IndexOfAnyExcept((byte)0) < 0)
            {
                bodies[i] = [];
                continue;
            }
            using var packed = new MemoryStream();
            using (var deflate = new DeflateStream(packed, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(piece);
            bodies[i] = packed.ToArray();
        }
        int headerLength = 4 + 4 + 8 + 4 + 4 * chunks;
        byte[] file = new byte[headerLength + bodies.Sum(b => (long)b.Length)];
        Magic.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), from);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(8), kept.LongLength);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), chunks);
        int at = headerLength;
        for (int i = 0; i < chunks; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(20 + 4 * i), bodies[i].Length);
            bodies[i].CopyTo(file, at);
            at += bodies[i].Length;
        }
        AtomicFile.Write(path, file);
    }

    /// <summary>What the file holds, or null when it is no night texture file (or a damaged one).</summary>
    public static Contents? ReadContents(SafeFileHandle file)
    {
        Span<byte> head = stackalloc byte[20];
        long fileLength = RandomAccess.GetLength(file);
        if (RandomAccess.Read(file, head, 0) < head.Length || !head[..4].SequenceEqual(Magic)) return null;
        int from = BinaryPrimitives.ReadInt32LittleEndian(head[4..]), chunks = BinaryPrimitives.ReadInt32LittleEndian(head[16..]);
        long length = BinaryPrimitives.ReadInt64LittleEndian(head[8..]);
        if (from < 0 || length < 0 || chunks != (length + Chunk - 1) / Chunk || 20 + 4L * chunks > fileLength) return null;
        byte[] table = new byte[4 * chunks];
        if (RandomAccess.Read(file, table, 20) < table.Length) return null;
        long[] start = new long[chunks];
        int[] lengths = new int[chunks];
        long at = 20 + table.Length;
        for (int i = 0; i < chunks; i++)
        {
            lengths[i] = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(4 * i));
            if (lengths[i] < 0) return null;
            start[i] = at;
            at += lengths[i];
        }
        return at == fileLength ? new Contents(from, length, start, lengths) : null;
    }

    /// <summary>The kept part's bytes from <paramref name="position"/> into <paramref name="output"/>, unpacking only the chunks they lie in.</summary>
    /// <exception cref="InvalidDataException">A chunk does not unpack.</exception>
    public static void Read(SafeFileHandle file, Contents contents, long position, Span<byte> output)
    {
        byte[] packed = ArrayPool<byte>.Shared.Rent(Chunk + 1024), plain = ArrayPool<byte>.Shared.Rent(Chunk);
        try
        {
            while (!output.IsEmpty)
            {
                int chunk = (int)(position / Chunk), within = (int)(position % Chunk);
                int count = Math.Min(output.Length, Math.Min(Chunk - within, (int)(contents.Length - position)));
                if (count <= 0) break;
                int size = contents.ChunkLength[chunk];
                if (size == 0)
                {
                    output[..count].Clear();
                }
                else
                {
                    if (size > packed.Length)
                    {
                        ArrayPool<byte>.Shared.Return(packed);
                        packed = ArrayPool<byte>.Shared.Rent(size);
                    }
                    if (RandomAccess.Read(file, packed.AsSpan(0, size), contents.ChunkStart[chunk]) < size) throw new InvalidDataException("a night texture file is cut short");
                    int plainLength = Unpack(packed.AsSpan(0, size), plain);
                    if (plainLength < within + count) throw new InvalidDataException("a night texture chunk is cut short");
                    plain.AsSpan(within, count).CopyTo(output);
                }
                output = output[count..];
                position += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packed);
            ArrayPool<byte>.Shared.Return(plain);
        }
    }

    private static int Unpack(ReadOnlySpan<byte> packed, byte[] plain)
    {
        using var source = new MemoryStream(packed.ToArray(), writable: false);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        int total = 0, read;
        while (total < Chunk && (read = deflate.Read(plain, total, Chunk - total)) > 0) total += read;
        return total;
    }
}
