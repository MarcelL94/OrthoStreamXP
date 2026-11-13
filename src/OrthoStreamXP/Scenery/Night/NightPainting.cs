using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// The night textures' painting, shared by every package: the painter, the workspaces it paints in, and how many
/// paintings run at once. They run on X-Plane's reads beside the photo builds; the fine ones (beside the aircraft, up
/// to 124 MB each) one at a time.
/// </summary>
internal sealed class NightPainting(NightPainter painter, int atOnce) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(atOnce), _large = new(1);
    private readonly NightWorkspaces _workspaces = new();

    /// <summary>
    /// Paints <paramref name="name"/> at <paramref name="side"/> pixels and gives the painting to <paramref name="keep"/>,
    /// whose answer is returned; null when the texture has no light at all. <paramref name="took"/>: the time it took,
    /// not counting the wait for its turn.
    /// </summary>
    public byte[]? Paint(TextureName name, int side, Func<NightWorkspace, byte[]> keep, out TimeSpan took)
    {
        bool large = side > NightWorkspaces.LargeSide;
        _gate.Wait();
        if (large) _large.Wait();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        NightWorkspace workspace = _workspaces.Rent(side);
        try
        {
            return painter.Paint(name, workspace) ? keep(workspace) : null;
        }
        finally
        {
            _workspaces.Return(workspace);
            took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (large) _large.Release();
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _large.Dispose();
    }
}
