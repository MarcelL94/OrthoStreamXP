namespace OrthoStreamXP.Tests;

public sealed class SettingsFileTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string Settings(string content) => _tree.WriteText("orthostreamxp.ini", content);

    [Fact]
    public void Lines_become_options_with_unquoted_windows_paths()
    {
        string file = Settings("""
            # comment
            ; also a comment
            [mount]
            source = C:\X-Plane 12\Custom Scenery\pkg
            level-change = -2
            track-aircraft = true
            background-textures = false
            prefetch-lookahead-seconds = 120
            lazy-texture-store =
            """);
        SettingValues parsed = SettingsOptions.Schema.ReadFile(file);

        Assert.Equal(@"C:\X-Plane 12\Custom Scenery\pkg", parsed.String("source"));
        Assert.Equal(-2, parsed.Int("level-change", 0));
        Assert.True(parsed.Flag("track-aircraft"));
        Assert.False(parsed.Flag("background-textures"));
        Assert.Equal(120, parsed.Double("prefetch-lookahead-seconds", 0));
        Assert.False(parsed.Has("lazy-texture-store"));
    }

    [Fact]
    public void An_option_with_two_values_takes_both_from_one_line()
    {
        var options = new SettingsSchema().Add("at", "a position", arity: 2);
        SettingValues parsed = options.ReadFile(Settings("at = 51.10872 16.86611"));
        Assert.Equal((51.10872, 16.86611), parsed.Pair("at"));
        var error = Assert.Throws<SettingsException>(() => options.ReadFile(Settings("at = 51.1")));
        Assert.Contains("line 1: 'at' needs 2 values", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none", "None")]
    [InlineData("console", "Console")]
    [InlineData("log-file", "File")]
    [InlineData("Both", "Both")]
    [InlineData("loud", null)]
    public void Log_output_values(string value, string? expected) => Assert.Equal(expected, Log.ParseOutput(value)?.ToString());

    [Theory]
    [InlineData("level-chnage = -2", "line 1: unknown setting 'level-chnage'")]
    [InlineData("track-aircraft = maybe", "line 1: 'track-aircraft' must be true or false")]
    [InlineData("just words", "line 1: expected 'name = value'")]
    [InlineData("texture-servers = 2", "line 1: unknown setting 'texture-servers'")] // removed with the Python version
    [InlineData("initial-position = 51.1 16.8", "line 1: unknown setting 'initial-position'")] // prefetch follows X-Plane now
    [InlineData("source-imagery-zoom = 16", "line 1: unknown setting 'source-imagery-zoom'")] // zooms come from the scenery
    [InlineData("texture-size = 1024", "line 1: unknown setting 'texture-size'")] // replaced long ago; the size now follows the level
    public void Mistakes_name_the_line(string line, string message)
    {
        var error = Assert.Throws<SettingsException>(() => SettingsOptions.Schema.ReadFile(Settings(line)));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }
}
