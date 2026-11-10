using System.Globalization;
using System.Net;
using System.Text.Json;

namespace OrthoStreamXP.Scenery;

/// <summary>The departure airport of a flight plan.</summary>
internal sealed record FlightOrigin(string Icao, double Lat, double Lon);

/// <summary>SimBrief's answer: the latest flight plan's origin (and destination), or what went wrong in plain words.</summary>
internal sealed record SimBriefAnswer(FlightOrigin? Origin, string? Destination, string? Problem)
{
    public static SimBriefAnswer Failed(string problem) => new(null, null, problem);
}

/// <summary>
/// Reads the latest flight plan (OFP) a SimBrief user generated, through SimBrief's public fetcher. Only the origin is
/// used. A failure is answered with its reason, never an exception: the flight plan is a hint, never a reason for the
/// mount to fail.
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

    /// <summary>The latest flight plan's origin, or null with the reason logged.</summary>
    public async Task<FlightOrigin?> LatestOriginAsync(string user, CancellationToken ct)
    {
        SimBriefAnswer answer = await FetchAsync(user, ct).ConfigureAwait(false);
        if (answer.Problem is { } problem) Log.Warning($"SimBrief: {problem}");
        return answer.Origin;
    }

    /// <summary>Asks SimBrief for the user's latest flight plan.</summary>
    public async Task<SimBriefAnswer> FetchAsync(string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user)) return SimBriefAnswer.Failed("no SimBrief username or Pilot ID is set");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using HttpResponseMessage response = await http.GetAsync(UrlFor(user), timeout.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            // SimBrief answers an unknown user with 400 and its reason in fetch.status.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
                return SimBriefAnswer.Failed($"SimBrief did not answer ({(int)response.StatusCode} {response.ReasonPhrase})");
            return Parse(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SimBriefAnswer.Failed($"SimBrief did not answer within {Timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            return SimBriefAnswer.Failed($"SimBrief could not be reached ({ex.Message})");
        }
    }

    /// <summary>The flight plan in SimBrief's JSON: its origin, or the reason there is none.</summary>
    internal static SimBriefAnswer Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? status = root.TryGetProperty("fetch", out JsonElement fetch) && fetch.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
            if (status is not null && !status.Equals("Success", StringComparison.OrdinalIgnoreCase))
                return SimBriefAnswer.Failed(status.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ? status[6..].Trim() : status);
            if (!root.TryGetProperty("origin", out JsonElement origin)
                || Number(origin, "pos_lat") is not { } lat || Number(origin, "pos_long") is not { } lon
                || lat is < -90 or > 90 || lon is < -180 or > 180)
                return SimBriefAnswer.Failed("the latest flight plan has no departure position");
            string? destination = root.TryGetProperty("destination", out JsonElement to) ? Text(to, "icao_code") : null;
            return new SimBriefAnswer(new FlightOrigin(Text(origin, "icao_code") ?? "", lat, lon), destination, null);
        }
        catch (JsonException)
        {
            return SimBriefAnswer.Failed("SimBrief's answer could not be read");
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
