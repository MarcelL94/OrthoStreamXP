using System.Collections.Concurrent;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// The pictures one night texture is painted and encoded in, <see cref="Side"/> pixels square: about 31 MB at 1024 px.
/// They are reused from one texture to the next (<see cref="NightWorkspaces"/>): made new for each, they were
/// large objects that set off a full garbage collection several times a second while a scenery loaded.
/// </summary>
internal sealed class NightWorkspace(int side)
{
    public int Side { get; } = side;
    public float[] Street { get; } = new float[side * side];
    public float[] Windows { get; } = new float[side * side];
    public float[] Sodium { get; } = new float[side * side];
    public float[] Led { get; } = new float[side * side];
    public float[] WarmGlow { get; } = new float[side * side];
    public float[] CoolGlow { get; } = new float[side * side];
    /// <summary>The blur's half-way picture.</summary>
    public float[] Across { get; } = new float[side * side];
    /// <summary>The painted texture's top level, RGB.</summary>
    public byte[] Rgb { get; } = new byte[side * side * 3];

    /// <summary>Ready for the next texture: the pictures painting draws into are black again.</summary>
    public void Clear()
    {
        Array.Clear(Street);
        Array.Clear(Windows);
        Array.Clear(Sodium);
        Array.Clear(Led);
        Array.Clear(Rgb);
    }
}

/// <summary>
/// The workspaces of every package's night textures, by side: one is taken for each painting and given back after it,
/// so there are never more than the paintings that run at once. Of the large ones (finer than
/// <see cref="LargeSide"/>, 124 MB at 2048 px, painted only beside the aircraft) one at most is kept.
/// </summary>
internal sealed class NightWorkspaces
{
    public const int LargeSide = 1024;

    private readonly ConcurrentDictionary<int, ConcurrentBag<NightWorkspace>> _free = new();

    public NightWorkspace Rent(int side)
    {
        if (_free.GetOrAdd(side, _ => []).TryTake(out NightWorkspace? workspace))
        {
            workspace.Clear();
            return workspace;
        }
        return new NightWorkspace(side);
    }

    public void Return(NightWorkspace workspace)
    {
        ConcurrentBag<NightWorkspace> free = _free.GetOrAdd(workspace.Side, _ => []);
        if (workspace.Side <= LargeSide || free.IsEmpty) free.Add(workspace);
    }
}
