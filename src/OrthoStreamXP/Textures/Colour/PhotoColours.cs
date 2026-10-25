namespace OrthoStreamXP.Textures;

/// <summary>
/// The colour adjustments of a photo texture, in the order they are made as X-Plane reads it: the colours evened out
/// between photos (<see cref="TextureColour"/>), then the season's (<see cref="Seasons"/>), then the exposure
/// (<see cref="Exposure"/>). The Settings page's preview makes them the same way, so it shows what X-Plane will.
/// </summary>
internal static class PhotoColours
{
    /// <summary>
    /// Adjusts the whole colour blocks in <paramref name="data"/>, which holds the bytes of a square DDS of
    /// <paramref name="size"/> pixels from <paramref name="fileOffset"/>; blocks cut by either end stay as they are.
    /// </summary>
    /// <param name="blockBytes">8 for DXT1 (BC1), 16 for DXT3 and DXT5.</param>
    public static void Apply(Span<byte> data, long fileOffset, int blockBytes, int size, TextureColour? colour, ushort[]? season, ushort[]? exposure)
    {
        colour?.Apply(data, fileOffset, blockBytes, size);
        if (season is not null) ColourTable.Apply(data, fileOffset, blockBytes, season);
        if (exposure is not null) ColourTable.Apply(data, fileOffset, blockBytes, exposure);
    }
}
