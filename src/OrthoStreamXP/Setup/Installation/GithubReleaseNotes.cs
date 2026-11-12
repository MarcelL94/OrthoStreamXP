using System.Text.Json;

namespace OrthoStreamXP.Setup;

/// <summary>
/// A version's release notes from its GitHub Release, for updates whose feed carries none (versions packed before the
/// notes went into the feed). Only the notes written for pilots (release-notes/vX.Y.Z.md): GitHub's generated list of
/// pull requests after them is left out; the release page has it.
/// </summary>
internal sealed class GithubReleaseNotes(HttpClient http, string repository)
{
    /// <summary>Where GitHub's generated list of changes begins in a release's text.</summary>
    private const string GeneratedHeading = "## What's Changed";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The notes of the release tagged v<paramref name="version"/>; null when it has none or GitHub does not answer (logged).</summary>
    public async Task<string?> FetchAsync(string version, CancellationToken cancel)
    {
        var url = new Uri($"https://api.github.com/repos/{repository}/releases/tags/v{Uri.EscapeDataString(version)}");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(RequestTimeout);
            using HttpResponseMessage response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            Log.Info($"The release notes of OrthoStreamXP {version} could not be read from GitHub: {ex.Message}");
            return null;
        }
    }

    /// <summary>The written notes in GitHub's JSON for one release, without the generated list; null when there are none.</summary>
    internal static string? Parse(string releaseJson)
    {
        using JsonDocument doc = JsonDocument.Parse(releaseJson);
        if (!doc.RootElement.TryGetProperty("body", out JsonElement body) || body.ValueKind != JsonValueKind.String) return null;
        string notes = body.GetString()!.ReplaceLineEndings("\n");
        int generated = notes.StartsWith(GeneratedHeading, StringComparison.Ordinal) ? 0 : notes.IndexOf("\n" + GeneratedHeading, StringComparison.Ordinal);
        if (generated >= 0) notes = notes[..generated];
        return string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }
}
