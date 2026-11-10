using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OrthoStreamXP.Setup;

/// <summary>
/// Downloads one large file to the disk, resuming what is already there with an HTTP range: an install of many gigabytes
/// survives a cut line, a stop and a restart of the program. Each answer is written as it comes. A file whose size or
/// published SHA-256 does not match is deleted, so the next try starts it afresh.
/// </summary>
internal sealed class AssetDownload(HttpClient http, int attempts = AssetDownload.DefaultAttempts, TimeSpan? stall = null)
{
    public const int DefaultAttempts = 5;
    private const int BufferSize = 1 << 20;
    private readonly TimeSpan _stall = stall ?? TimeSpan.FromSeconds(60);

    /// <summary>
    /// Makes <paramref name="target"/> the whole of <paramref name="asset"/>, downloading what is missing.
    /// <paramref name="progress"/> gets the bytes added since the last call (so a resumed file reports only the new bytes).
    /// </summary>
    /// <exception cref="IOException">Every attempt failed, or the file does not match its size or SHA-256.</exception>
    public async Task GetAsync(MeshAsset asset, string target, Action<long> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Exception? last = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (Length(target) > asset.Size) File.Delete(target);
                if (Length(target) < asset.Size) await DownloadRestAsync(asset, target, progress, cancel).ConfigureAwait(false);
                if (Length(target) != asset.Size)
                    throw new IOException($"{asset.Name} ended at {Length(target)} bytes of {asset.Size}");
                await CheckAsync(asset, target, cancel).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException && !cancel.IsCancellationRequested)
            {
                last = ex;
                if (ex is ChecksumException || attempt == attempts) break;
                Log.Warning($"Downloading {asset.Name} failed (attempt {attempt} of {attempts}): {ex.Message}; trying again");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 << attempt)), cancel).ConfigureAwait(false);
            }
        }
        throw new IOException($"{asset.Name} could not be downloaded: {last?.Message}", last);
    }

    private async Task DownloadRestAsync(MeshAsset asset, string target, Action<long> progress, CancellationToken cancel)
    {
        long have = Length(target);
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stalled.CancelAfter(_stall);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stalled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"{asset.Name}: no answer for {_stall.TotalSeconds:F0} s");
        }
        using (response)
        {
            response.EnsureSuccessStatusCode();
            // A server that ignores the range sends the whole file again: it is written from the start.
            bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (have > 0 && !resumed)
            {
                progress(-have);
                have = 0;
            }
            using var file = new FileStream(target, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize);
            using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            byte[] buffer = new byte[BufferSize];
            while (true)
            {
                stalled.CancelAfter(_stall); // the limit is on a silence, not on the whole file
                int read;
                try
                {
                    read = await body.ReadAsync(buffer, stalled.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new TimeoutException($"{asset.Name}: nothing received for {_stall.TotalSeconds:F0} s");
                }
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                progress(read);
            }
        }
    }

    /// <summary>Checks the file against its published SHA-256 (only some regions publish one); a mismatch deletes it.</summary>
    private async Task CheckAsync(MeshAsset asset, string target, CancellationToken cancel)
    {
        if (asset.Sha256Url is null) return;
        string published;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            timeout.CancelAfter(_stall);
            try
            {
                published = await http.GetStringAsync(new Uri(asset.Sha256Url), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new TimeoutException($"{asset.Name}: its SHA-256 did not come within {_stall.TotalSeconds:F0} s");
            }
        }
        string expected = published.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        string actual;
        using (FileStream file = File.OpenRead(target))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false));
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return;
        File.Delete(target);
        throw new ChecksumException($"{asset.Name} does not match its published SHA-256 (expected {expected}, got {actual}); it was deleted");
    }

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>A file that arrived whole but differs from what was published: trying again at once would not help.</summary>
    private sealed class ChecksumException(string message) : IOException(message);
}
