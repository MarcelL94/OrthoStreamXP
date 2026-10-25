namespace OrthoStreamXP.Textures;

/// <summary>What a <see cref="WeightedPriorityGate"/>'s most urgent waiter waits for.</summary>
internal enum GateLimit
{
    None,
    /// <summary>The first budget (the build memory).</summary>
    First,
    /// <summary>The second budget (the build tiles).</summary>
    Second,
    /// <summary>Every place is taken.</summary>
    Entries,
}
