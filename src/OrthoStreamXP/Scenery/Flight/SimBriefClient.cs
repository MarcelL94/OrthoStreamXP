using System.Globalization;
using System.Net;
using System.Text.Json;

namespace OrthoStreamXP.Scenery;

/// <summary>SimBrief's answer: the latest flight plan, or what went wrong in plain words.</summary>
internal sealed record SimBriefAnswer(FlightPlan? Plan, string? Problem)
{
    public FlightOrigin? Origin => Plan?.Origin;

    public string? Destination => Plan?.Destination?.Icao;

    public static SimBriefAnswer Failed(string problem) => new(null, problem);
}

/// <summary>
/// Reads the latest flight plan (OFP) a SimBrief user generated, through SimBrief's public fetcher: the departure,
/// arrival and alternate airports and the navlog. A failure is answered with its reason, never an exception: the flight plan is a hint, never a reason for the
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

    /// <summary>The latest flight plan, or null with the reason logged.</summary>
    public async Task<FlightPlan?> LatestPlanAsync(string user, CancellationToken ct)
    {
        SimBriefAnswer answer = await FetchAsync(user, ct).ConfigureAwait(false);
        if (answer.Problem is { } problem) Log.Warning($"SimBrief: {problem}");
        return answer.Plan;
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

    /// <summary>The flight plan in SimBrief's JSON, or the reason there is none.</summary>
    internal static SimBriefAnswer Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? status = root.TryGetProperty("fetch", out JsonElement fetch) && fetch.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
            if (status is not null && !status.Equals("Success", StringComparison.OrdinalIgnoreCase))
                return SimBriefAnswer.Failed(status.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ? status[6..].Trim() : status);
            if (Airport(root, "origin") is not { } origin) return SimBriefAnswer.Failed("the latest flight plan has no departure position");
            return new SimBriefAnswer(new FlightPlan(origin, Airport(root, "destination"), Airport(root, "alternate"), Route(root), Generated(root)), null);
        }
        catch (JsonException)
        {
            return SimBriefAnswer.Failed("SimBrief's answer could not be read");
        }
    }

    /// <summary>An airport by its key; SimBrief gives several alternates as an array (the first is taken).</summary>
    private static FlightOrigin? Airport(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement airport)) return null;
        if (airport.ValueKind == JsonValueKind.Array) airport = airport.EnumerateArray().FirstOrDefault();
        if (airport.ValueKind != JsonValueKind.Object || Number(airport, "pos_lat") is not { } lat || Number(airport, "pos_long") is not { } lon
            || lat is < -90 or > 90 || lon is < -180 or > 180) return null;
        return new FlightOrigin(Text(airport, "icao_code") ?? "", lat, lon, Number(airport, "elevation") ?? 0);
    }

    /// <summary>When the plan was generated: <c>params.time_generated</c>, Unix seconds (as a string, like the coordinates).</summary>
    private static DateTimeOffset? Generated(JsonElement root)
    {
        if (!root.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object
            || Number(parameters, "time_generated") is not { } seconds || seconds is <= 0 or > 32503680000) return null; // > year 3000: not a time
        return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
    }

    /// <summary>The navlog's points in order; one point comes as an object, not an array.</summary>
    private static List<RoutePoint> Route(JsonElement root)
    {
        if (!root.TryGetProperty("navlog", out JsonElement navlog) || !navlog.TryGetProperty("fix", out JsonElement fixes)) return [];
        IEnumerable<JsonElement> all = fixes.ValueKind switch
        {
            JsonValueKind.Array => fixes.EnumerateArray(),
            JsonValueKind.Object => [fixes],
            _ => [],
        };
        var route = new List<RoutePoint>();
        foreach (JsonElement fix in all)
            if (Number(fix, "pos_lat") is { } lat && Number(fix, "pos_long") is { } lon && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
                route.Add(new RoutePoint(Text(fix, "ident") ?? "", lat, lon, Number(fix, "altitude_feet") ?? 0,
                    (Number(fix, "time_total") ?? 0) / 60, Text(fix, "stage") ?? ""));
        return route;
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
