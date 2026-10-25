using SkiaSharp;
using System.Net;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

public sealed class ImageryProviderTests
{
    private const string Fallback = "http://fallback.test/{z}/{x}/{y}";

    [Fact]
    public void Built_in_providers_are_listed_in_the_order_given()
    {
        var providers = ImageryProvider.ParseList("esri, bing", null);
        Assert.Equal(["esri", "bing"], providers.Select(p => p.Name));
        Assert.Contains("arcgisonline.com", providers[0].Template, StringComparison.Ordinal);
        Assert.Contains("virtualearth.net", providers[1].Template, StringComparison.Ordinal);
    }

    [Fact]
    public void The_one_name_that_is_not_built_in_uses_the_url()
    {
        var providers = ImageryProvider.ParseList("bing,mine", Fallback);
        Assert.Equal(Fallback, providers[1].Template);
    }

    [Fact]
    public void A_lone_name_uses_the_url_even_when_built_in()
    {
        Assert.Equal(Fallback, Assert.Single(ImageryProvider.ParseList("bing", Fallback)).Template);
    }

    [Theory]
    [InlineData("mine", null)]                // needs --texture-url
    [InlineData("bing,mine,yours", Fallback)] // only one can use it
    [InlineData("bing,esri", Fallback)]       // nothing uses it
    [InlineData("bing,synthetic", null)]
    [InlineData("bing,Bing", null)]
    [InlineData(" , ", null)]
    public void Unusable_lists_are_refused(string list, string? url)
    {
        Assert.Throws<ArgumentException>(() => ImageryProvider.ParseList(list, url));
    }

    [Fact]
    public async Task A_tile_the_first_provider_lacks_comes_from_the_next()
    {
        var server = new FakeTileServer(statusOf: uri => uri.Host == "fallback.test" ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        using var tiles = new TileSource([new("first", FakeTileServer.Template), new("second", Fallback)], 4, handler: server);
        var stats = new TileStats();

        byte[] rgb = await tiles.GetTileAsync(1, 2, 3, priority: false, stats);

        Assert.Equal([40, 90, 160], rgb[..3]);
        Assert.Equal(["t3.test", "fallback.test"], server.Requests.Select(u => u.Host)); // a 404 is not retried
        Assert.Equal(1, stats.Fallbacks);
    }

    [Fact]
    public async Task Bings_no_imagery_placeholder_counts_as_missing()
    {
        var server = new FakeTileServer(noTile: uri => uri.Host != "fallback.test");
        using var tiles = new TileSource([new("bing", FakeTileServer.Template), new("second", Fallback)], 4, handler: server);
        var stats = new TileStats();

        await tiles.GetTileAsync(1, 2, 3, priority: false, stats);

        Assert.Equal(["t3.test", "fallback.test"], server.Requests.Select(u => u.Host));
        Assert.Equal(1, stats.Fallbacks);
    }

    [Fact]
    public async Task A_transparent_tile_counts_as_missing()
    {
        var server = new FakeTileServer(transparent: uri => uri.Host != "fallback.test");
        using var tiles = new TileSource([new("first", FakeTileServer.Template), new("second", Fallback)], 4, handler: server);
        var stats = new TileStats();

        byte[] rgb = await tiles.GetTileAsync(1, 2, 3, priority: false, stats);

        Assert.Equal([40, 90, 160], rgb[..3]);
        Assert.Equal(["t3.test", "fallback.test"], server.Requests.Select(u => u.Host));
        Assert.Equal(1, stats.Fallbacks);
    }

    [Fact]
    public async Task Geoportal_is_asked_only_for_tiles_in_Poland()
    {
        var server = new FakeTileServer();
        using var tiles = new TileSource([new("geoportal", FakeTileServer.Template), new("second", Fallback)], 4, handler: server);
        var (wroclawX, wroclawY) = Tiles.LatLonToTile(51.1, 17.0, 16);
        var (parisX, parisY) = Tiles.LatLonToTile(48.86, 2.35, 16);

        await tiles.GetTileAsync(wroclawX, wroclawY, 16, priority: false, new TileStats());
        await tiles.GetTileAsync(parisX, parisY, 16, priority: false, new TileStats());

        Assert.Equal(["t" + (wroclawX + wroclawY) % 8 + ".test", "fallback.test"], server.Requests.Select(u => u.Host));
    }

    [Fact]
    public void Only_an_image_with_a_see_through_pixel_is_transparent()
    {
        Assert.True(Images.HasTransparentPixels(FakeTileServer.TransparentTile));
        using var bitmap = new SKBitmap(256, 256);
        bitmap.Erase(new SKColor(40, 90, 160));
        using (SKData opaque = bitmap.Encode(SKEncodedImageFormat.Png, 100)) Assert.False(Images.HasTransparentPixels(opaque.ToArray()));
        using (SKData jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90)) Assert.False(Images.HasTransparentPixels(jpeg.ToArray()));
        bitmap.SetPixel(255, 255, SKColors.Transparent); // one corner outside the country
        using (SKData edge = bitmap.Encode(SKEncodedImageFormat.Png, 100)) Assert.True(Images.HasTransparentPixels(edge.ToArray()));
    }

    [Fact]
    public async Task Bings_placeholder_is_used_when_no_provider_has_the_tile()
    {
        var server = new FakeTileServer(statusOf: uri => uri.Host == "fallback.test" ? HttpStatusCode.NotFound : HttpStatusCode.OK,
            noTile: uri => uri.Host != "fallback.test");
        using var tiles = new TileSource([new("bing", FakeTileServer.Template), new("second", Fallback)], 4, handler: server);
        var stats = new TileStats();

        byte[] rgb = await tiles.GetTileAsync(1, 2, 3, priority: false, stats);

        Assert.Equal([40, 90, 160], rgb[..3]);
        Assert.Equal(0, stats.Fallbacks);
    }

    [Fact]
    public async Task A_tile_no_provider_has_names_each_failure()
    {
        var server = new FakeTileServer(status: _ => HttpStatusCode.NotFound);
        using var tiles = new TileSource([new("first", FakeTileServer.Template), new("second", Fallback)], 4, handler: server);

        IOException error = await Assert.ThrowsAsync<IOException>(() => tiles.GetTileAsync(1, 2, 3, false, new TileStats()));

        Assert.Contains("first:", error.Message, StringComparison.Ordinal);
        Assert.Contains("second:", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, server.Requests.Count);
    }
}
