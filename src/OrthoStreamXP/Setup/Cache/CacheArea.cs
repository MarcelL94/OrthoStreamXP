using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Setup;

/// <summary>What the cache holds of one area: its bytes by setup (quality) folder, its files, and when it was last flown.</summary>
internal sealed record CacheArea(AreaName Name, IReadOnlyDictionary<string, long> BytesBySetup, int Files, DateTime LastUsedUtc)
{
    public long Bytes => BytesBySetup.Values.Sum();
}
