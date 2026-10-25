namespace OrthoStreamXP.Scenery;

/// <summary>How far a texture is in the paging model's terms, which decides what one of its reads may cost.</summary>
internal enum Band
{
    /// <summary>X-Plane wants level 0 or 1: the read waits for real imagery, long at the start of a flight.</summary>
    Near,
    /// <summary>A reduced level: fetched at that level's own zoom, with a short wait.</summary>
    Middle,
    /// <summary>A level the low-zoom base covers: answered at once.</summary>
    Far,
}
