namespace OrthoStreamXP.Scenery;

/// <param name="NearWaitSeconds">How long a near or middle read may wait while a flight starts.</param>
/// <param name="MaxWaitSeconds">How long a near or middle read may wait in flight before it gets a stand-in (AutoOrtho's maxwait).</param>
/// <param name="BuildMemoryBytes">
/// Memory the builds of more than the base tile may hold together while their tiles arrive (--build-memory). Each
/// counts at its own size (<see cref="TextureStreamer.BuildWeight"/>): the few full-size builds next to the aircraft
/// share it with the many small ones far scenery needs while a flight loads.
/// </param>
/// <param name="BuildTiles">
/// Tiles the builds may download together (--build-tiles): finer imagery makes a texture cost more tiles (4096 for a
/// ZL16 texture at zoom 18, 1024 at zoom 17), so this keeps the full-size builds from filling the connection. Builds
/// done ahead of X-Plane may use only half of it: the other half stays free for the reads X-Plane is waiting for,
/// which never have to wait for an ahead build to finish.
/// </param>
/// <param name="MaxBuilds">Builds at once whatever their size: a few dozen already keep every download worker busy.</param>
internal sealed record StreamingOptions(double NearWaitSeconds, double MaxWaitSeconds, long RamBudgetBytes = 1L << 30,
    long BuildMemoryBytes = 512L << 20, long BuildTiles = TextureStreamer.DefaultBuildTiles, int MaxBuilds = TextureStreamer.DefaultMaxBuilds);
