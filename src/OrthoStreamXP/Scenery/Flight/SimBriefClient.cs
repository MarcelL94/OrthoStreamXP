using System.Globalization;
using System.Net;
using System.Text.Json;

namespace OrthoStreamXP.Scenery;

/// <summary>The departure airport of a flight plan.</summary>
internal sealed record FlightOrigin(string Icao, double Lat, double Lon);

/// <summary>
/// Reads the latest flight plan (OFP) a SimBrief user generated, through SimBrief's public fetcher. Only the origin is
/// used. A failure is logged and answered with null: the flight plan is a hint, never a reason for the mount to fail.
/// </summary>
internal sealed class SimBriefClient(HttpClient http)
{
    public const string FetcherUrl = "https://www.simbrief.com/api/xml.fetcher.php";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The fetcher's address for a username, or for the numeric Pilot ID when <paramref name="user"/> is all digits.</summary>
    public static Uri UrlFor(string user)
    {
        string trimmed = user.Trim();
        string key = trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit) ? "userid" : "username";
        return new Uri($"{FetcherUrl}?{key}={Uri.EscapeDataString(trimmed)}&json=1");
    }

    public async Task<FlightOrigin?> LatestOriginAsync(string user, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using HttpResponseMessage response = await http.GetAsync(UrlFor(user), timeout.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            // SimBrief answers an unknown user with 400 and its reason in fetch.status.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
            {
                Log.Warning($"SimBrief: no flight plan ({(int)response.StatusCode} {response.ReasonPhrase})");
                return null;
            }
            return Parse(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning($"SimBrief: no answer within {Timeout.TotalSeconds:0} s");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log.Warning($"SimBrief: could not fetch the flight plan ({ex.Message})");
            return null;
        }
    }

    /// <summary>The origin of an OFP in SimBrief's JSON, or null (logged) when SimBrief reports an error or it has none.</summary>
    internal static FlightOrigin? Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? status = root.TryGetProperty("fetch", out JsonElement fetch) && fetch.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
            if (status is not null && !status.Equals("Success", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning($"SimBrief: {status}");
                return null;
            }
            if (!root.TryGetProperty("origin", out JsonElement origin)
                || Number(origin, "pos_lat") is not { } lat || Number(origin, "pos_long") is not { } lon
                || lat is < -90 or > 90 || lon is < -180 or > 180)
            {
                Log.Warning("SimBrief: the latest flight plan has no departure position");
                return null;
            }
            string icao = origin.TryGetProperty("icao_code", out JsonElement code) ? code.GetString() ?? "" : "";
            return new FlightOrigin(icao, lat, lon);
        }
        catch (JsonException ex)
        {
            Log.Warning($"SimBrief: unreadable answer ({ex.Message})");
            return null;
        }
    }

    /// <summary>A coordinate: SimBrief sends them as strings ("48.353783"), a number is taken too.</summary>
    private static double? Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
            _ => null,
        };
    }
}
