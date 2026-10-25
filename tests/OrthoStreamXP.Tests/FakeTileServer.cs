using System.Collections.Concurrent;
using System.Net;
using SkiaSharp;

namespace OrthoStreamXP.Tests;

/// <summary>
/// Stands in for XYZ tile servers ("http://t{s}.test/{z}/{x}/{y}", or any host with the same path) and records every
/// request. <paramref name="status"/> answers by zoom, <paramref name="statusOf"/> by the whole URL;
/// <paramref name="noTile"/> marks a 200 answer as Bing's "no imagery here" placeholder; <paramref name="transparent"/>
/// answers Geoportal's transparent tile instead of a picture; <paramref name="hold"/> keeps a tile's answer back until the
/// task it gives for that URL ends.
/// </summary>
internal sealed class FakeTileServer(Func<int, HttpStatusCode>? status = null, Func<int, TimeSpan>? delay = null,
    Func<Uri, HttpStatusCode>? statusOf = null, Func<Uri, bool>? noTile = null, Func<Uri, bool>? transparent = null,
    Func<Uri, Task?>? hold = null) : HttpMessageHandler
{
    public const string Template = "http://t{s}.test/{z}/{x}/{y}";

    private static readonly byte[] Tile = EncodeTile();
    public static readonly byte[] TransparentTile = EncodeTile(SKColors.Transparent);

    public ConcurrentQueue<Uri> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        Requests.Enqueue(uri);
        int zoom = int.Parse(uri.Segments[1].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture);
        if (delay?.Invoke(zoom) is { } wait && wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
        if (hold?.Invoke(uri) is { } held) await held.WaitAsync(cancellationToken);
        HttpStatusCode code = statusOf?.Invoke(uri) ?? status?.Invoke(zoom) ?? HttpStatusCode.OK;
        var response = new HttpResponseMessage(code) { Content = new ByteArrayContent(code != HttpStatusCode.OK ? [] : transparent?.Invoke(uri) == true ? TransparentTile : Tile) };
        if (noTile?.Invoke(uri) == true) response.Headers.Add("X-VE-Tile-Info", "no-tile"); // Bing's placeholder
        return response;
    }

    private static byte[] EncodeTile(SKColor? colour = null)
    {
        using var bitmap = new SKBitmap(256, 256);
        bitmap.Erase(colour ?? new SKColor(40, 90, 160));
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
