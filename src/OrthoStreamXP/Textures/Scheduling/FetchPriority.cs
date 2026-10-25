namespace OrthoStreamXP.Textures;

/// <summary>
/// How urgent a download is; lower numbers go first. X-Plane waiting next to the aircraft comes before
/// everything, then X-Plane waiting elsewhere (bigger mip levels first), then work done before X-Plane asks.
/// </summary>
internal static class FetchPriority
{
    /// <summary>X-Plane is waiting for a texture next to the aircraft.</summary>
    public const int NearRead = 0;
    /// <summary>Next to the aircraft, fetched before X-Plane asks.</summary>
    public const int NearWarm = 100;
    /// <summary>X-Plane is waiting for a mip level farther out; add the level, so bigger levels go first.</summary>
    public const int Read = 200;
    /// <summary>Fetched before X-Plane asks: the middle distance, the flight path, a whole texture built ahead.</summary>
    public const int Warm = 300;
    /// <summary>Far scenery's low-zoom tile, or the real data behind a stand-in X-Plane already has.</summary>
    public const int Background = 400;

    public static int ForRead(int mip) => Read + Math.Clamp(mip, 0, 99);
}
