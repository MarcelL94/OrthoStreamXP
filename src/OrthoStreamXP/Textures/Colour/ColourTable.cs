using System.Buffers.Binary;

namespace OrthoStreamXP.Textures;

/// <summary>
/// Recolouring of BC1 colour blocks by a lookup table over the 65,536 RGB565 colours, as X-Plane reads a DDS: the seasons,
/// the exposure and the colour matching all change a block by its two colours, which changes every pixel of it.
/// </summary>
internal static class ColourTable
{
    /// <summary>
    /// Recolours the whole colour blocks in <paramref name="data"/>, which holds a DDS's bytes from
    /// <paramref name="fileOffset"/>. A block cut by either end of the buffer stays as it is, so its two halves always
    /// match whichever reads they come in. A BC1 block keeps its opaque four-colour mode (the first colour the larger
    /// one), swapping the two colours and their pixel indices where the table turned their order around.
    /// </summary>
    /// <param name="blockBytes">8 for DXT1 (BC1), 16 for DXT3 and DXT5 (their colour block is the second half).</param>
    public static void Apply(Span<byte> data, long fileOffset, int blockBytes, ushort[] table)
    {
        long from = Math.Max(fileOffset, Dds.HeaderSize) - Dds.HeaderSize;
        long first = Dds.HeaderSize + (from + blockBytes - 1) / blockBytes * blockBytes, end = fileOffset + data.Length;
        int colorAt = blockBytes == 16 ? 8 : 0;
        for (long at = first; at + blockBytes <= end; at += blockBytes)
        {
            Span<byte> block = data.Slice((int)(at - fileOffset) + colorAt, 8);
            ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block), c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
            if (blockBytes == 8 && c0 <= c1) continue; // three colours and transparent: left as made
            WriteColors(block, blockBytes, table[c0], table[c1]);
        }
    }

    /// <summary>
    /// Writes a colour block's two new colours. BC1 keeps its opaque four-colour mode (the first colour the larger):
    /// where the new colours came out the other way round, they are swapped with their pixel indices.
    /// </summary>
    internal static void WriteColors(Span<byte> block, int blockBytes, ushort n0, ushort n1)
    {
        if (blockBytes == 8 && n0 < n1)
        {
            // Swapped colours: index 0 <-> 1 and 2 <-> 3, which is flipping the low bit of every index.
            (n0, n1) = (n1, n0);
            for (int i = 4; i < 8; i++) block[i] ^= 0x55;
        }
        else if (blockBytes == 8 && n0 == n1)
        {
            // One colour: every pixel index 0, and the unused second colour one step below, which keeps the opaque mode.
            block[4..].Clear();
            if (n0 > 0) n1 = (ushort)(n0 - 1);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(block, n0);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], n1);
    }

    /// <summary>The colour-block size of a DDS by its header's FourCC (bytes 84-87): 8, 16, or 0 for one this does not change.</summary>
    public static int BlockBytes(ReadOnlySpan<byte> fourCc) =>
        fourCc.SequenceEqual("DXT1"u8) ? 8 : fourCc.SequenceEqual("DXT3"u8) || fourCc.SequenceEqual("DXT5"u8) ? 16 : 0;

    internal static (double R, double G, double B) Decode565(int c) =>
        (((c >> 11) & 31) * 255.0 / 31, ((c >> 5) & 63) * 255.0 / 63, (c & 31) * 255.0 / 31);

    internal static int Encode565(double r, double g, double b) =>
        ((int)Math.Round(Math.Clamp(r, 0, 255) * 31 / 255) << 11) | ((int)Math.Round(Math.Clamp(g, 0, 255) * 63 / 255) << 5)
        | (int)Math.Round(Math.Clamp(b, 0, 255) * 31 / 255);
}
