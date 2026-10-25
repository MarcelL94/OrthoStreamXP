namespace OrthoStreamXP.Scenery;

/// <summary>What the bytes of a read were.</summary>
internal enum Served
{
    /// <summary>Real imagery at the detail of every level read.</summary>
    Real,
    /// <summary>Some levels were stretched from coarser imagery.</summary>
    StandIn,
    /// <summary>Some levels were one flat colour: nothing of the place was at hand.</summary>
    Flat,
}
