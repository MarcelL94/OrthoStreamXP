using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrthoStreamXP.Setup;

/// <summary>
/// The regions of AutoOrtho's scenery mesh, read from the GitHub releases of kubilus1/autoortho-scenery the way AutoOrtho's
/// own installer reads them: a release belongs to a region when it carries that region's <c>&lt;id&gt;_info.json</c>, the
/// newest one counts, and pre-releases and drafts do not. The list is kept beside the settings and asked for again at most
/// every hour; it stands in when GitHub does not answer.
/// </summary>
internal sealed partial class MeshCatalog(HttpClient http, string cacheFile)
{
    public const string Repository = "kubilus1/autoortho-scenery";
    public const string ProjectPage = "https://github.com/" + Repository;
    private const string ReleasesUrl = "https://api.github.com/repos/" + Repository + "/releases?per_page=100";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1), RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = true };

    /// <summary>z_eur_03.zip (a mesh zip), y_eur_overlays.zip.02 (a part of the overlays zip) or y_afr_overlays.zip (in one piece).</summary>
    [GeneratedRegex(@"^(?<kind>[yz])_(?<name>.+?)\.zip(?:\.(?<part>\d+))?$")]
    private static partial Regex AssetPattern();

    /// <summary>The regions, from the list kept when it is recent (unless <paramref name="refresh"/>), else from GitHub.</summary>
    /// <exception cref="IOException">GitHub did not answer and no list was kept before.</exception>
    public async Task<IReadOnlyList<MeshRegion>> LoadAsync(bool refresh, CancellationToken cancel)
    {
        bool fresh = !refresh && File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < MaxAge;
        if (fresh && ReadCache() is { } kept) return kept;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(RequestTimeout);
            using HttpResponseMessage response = await http.GetAsync(new Uri(ReleasesUrl), timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            IReadOnlyList<MeshRegion> regions = Parse(json);
            if (regions.Count > 0) WriteCache(regions);
            return regions;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            if (ReadCache() is { } older)
            {
                Log.Warning($"GitHub's list of mesh regions could not be read ({ex.Message}); the list kept from before is used");
                return older;
            }
            throw new IOException($"The list of mesh regions could not be read from GitHub: {ex.Message}", ex);
        }
    }

    /// <summary>The newest release of each known region in GitHub's list of releases (JSON, newest first).</summary>
    internal static IReadOnlyList<MeshRegion> Parse(string releasesJson)
    {
        using JsonDocument doc = JsonDocument.Parse(releasesJson);
        var regions = new Dictionary<string, MeshRegion>();
        foreach (JsonElement release in doc.RootElement.EnumerateArray())
        {
            if (Bool(release, "prerelease") || Bool(release, "draft")) continue;
            var assets = new List<(string Name, string Url, long Size)>();
            if (release.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                foreach (JsonElement a in list.EnumerateArray())
                    if (Text(a, "name") is { } name && Text(a, "browser_download_url") is { } url && a.TryGetProperty("size", out JsonElement size))
                        assets.Add((name, url, size.GetInt64()));
            string? id = assets.Select(a => a.Name).FirstOrDefault(n => n.EndsWith("_info.json", StringComparison.Ordinal))?[..^"_info.json".Length];
            if (id is null || !MeshRegion.IsKnown(id) || regions.ContainsKey(id)) continue;
            if (Of(id, Text(release, "tag_name") ?? "", assets) is { } region) regions[id] = region;
        }
        return [.. MeshRegion.Known.Where(k => regions.ContainsKey(k.Id)).Select(k => regions[k.Id])];
    }

    private static MeshRegion? Of(string id, string release, List<(string Name, string Url, long Size)> assets)
    {
        var urls = assets.ToDictionary(a => a.Name, a => a.Url, StringComparer.Ordinal);
        var mesh = new List<MeshAsset>();
        var overlays = new List<(int Part, MeshAsset Asset)>();
        foreach (var (name, url, size) in assets)
        {
            Match m = AssetPattern().Match(name);
            if (!m.Success) continue;
            string? sha = urls.GetValueOrDefault(name + ".sha256");
            if (m.Groups["kind"].Value == "z" && m.Groups["name"].Value.StartsWith(id + "_", StringComparison.Ordinal) && !m.Groups["part"].Success)
                mesh.Add(new MeshAsset(name, url, size, sha));
            else if (m.Groups["kind"].Value == "y" && m.Groups["name"].Value == id + "_overlays")
                overlays.Add((m.Groups["part"].Success ? int.Parse(m.Groups["part"].Value, CultureInfo.InvariantCulture) : 0, new MeshAsset(name, url, size, sha)));
        }
        if (mesh.Count == 0) return null;
        return new MeshRegion(id, release, [.. mesh.OrderBy(a => a.Name, StringComparer.Ordinal)], [.. overlays.OrderBy(o => o.Part).Select(o => o.Asset)]);
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private List<MeshRegion>? ReadCache()
    {
        try
        {
            return File.Exists(cacheFile) ? JsonSerializer.Deserialize<List<MeshRegion>>(File.ReadAllText(cacheFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void WriteCache(IReadOnlyList<MeshRegion> regions)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            AtomicFile.Write(cacheFile, JsonSerializer.SerializeToUtf8Bytes(regions, CacheJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not keep the list of mesh regions: {ex.Message}");
        }
    }
}
