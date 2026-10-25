namespace OrthoStreamXP.Scenery;

/// <summary>
/// How one read of a streamed texture was answered, for the read log. <paramref name="Wanted"/> is the level X-Plane
/// was after (null for the header chunk and later pieces of a level).
/// </summary>
internal readonly record struct ReadAnswer(int Bytes, int? FirstMip = null, int? LastMip = null, Band? Band = null,
    Served? Served = null, double WaitMs = 0, double? DistanceM = null, int? Wanted = null);
