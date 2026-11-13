using OrthoStreamXP.Gui;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Tests;

/// <summary>What the window relies on: editing orthostreamxp.ini in place, the presets, the cache listing and plain-word errors.</summary>
public sealed class WindowSettingsTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void A_changed_value_keeps_the_line_and_the_comments_around_it()
    {
        var settings = SettingsDocument.FromText("x.ini", "# where the cache is\ncache              = C:\\old\n\n# speed\ndownload-workers = 8\n");

        settings.Set("cache", @"D:\OrthoStreamXP-cache");
        settings.Set("download-workers", 160);

        Assert.Equal(["# where the cache is", @"cache              = D:\OrthoStreamXP-cache", "", "# speed", "download-workers = 160"], settings.Lines);
    }

    [Fact]
    public void A_new_setting_goes_at_the_end_and_null_empties_one()
    {
        var settings = SettingsDocument.FromText("x.ini", "cache = C:\\c\nlog-output = both");

        settings.Set("texture-quality", "high");
        settings.Set("level-change", "-1");
        settings.Set("log-output", null);

        Assert.Equal(["cache = C:\\c", "log-output =", "", "# --- Set in the OrthoStreamXP window", "texture-quality    = high", "level-change       = -1"], settings.Lines);
        Assert.Null(settings.Get("log-output")); // empty means not set
        Assert.Equal(-1, settings.GetInt("level-change"));
    }

    [Fact]
    public void The_last_line_of_a_setting_counts_as_on_the_command_line()
    {
        var settings = SettingsDocument.FromText("x.ini", "level-change = -2\nlevel-change = 0\n");
        Assert.Equal(0, settings.GetInt("level-change"));
        settings.Set("level-change", "-1");
        Assert.Equal(["level-change = -2", "level-change = -1"], settings.Lines);
    }

    [Fact]
    public void A_new_settings_file_is_a_valid_mount_configuration()
    {
        string path = _tree.PathOf("orthostreamxp.ini");
        SettingsDocument settings = SettingsDocument.CreateNew(path, @"C:\X-Plane 12", @"C:\XPME\BP", @"D:\cache", QualityPreset.Sharp);
        settings.Save();

        SettingValues args = SettingsDocument.Load(path).Parse();

        Assert.Equal(-1, args.Int("level-change", 0));
        Assert.Null(args.String("texture-quality")); // the size follows the level
        Assert.Equal(@"D:\cache", args.String("cache"));
        Assert.True(args.Flag("track-aircraft"));
        Assert.Equal("5", args.String("provisional-wait"));
    }

    [Fact]
    public void An_older_settings_file_with_texture_quality_still_loads()
    {
        string path = _tree.WriteText("old.ini", "cache = C:\\c\nlevel-change = 0\ntexture-quality = high\n");
        SettingValues args = SettingsDocument.Load(path).Parse(); // no "unknown setting" error
        Assert.Equal("high", args.String("texture-quality"));
        Assert.Equal(4096, new OrthoStreamXP.Textures.TexturePlan(args.Int("level-change", 0)).SizeFor(16)); // Ultra's size, whatever it says
    }

    [Theory]
    [InlineData("orthostreamxp-2.ini", -2)]
    [InlineData("orthostreamxp-1.ini", -1)]
    [InlineData("orthostreamxp-0.ini", 0)]
    [InlineData("orthostreamxp+1.ini", 1)]
    public void Each_preset_has_the_values_of_its_example_file(string file, int level)
    {
        SettingsDocument example = SettingsDocument.Load(Path.Join(AppContext.BaseDirectory, "examples", file));
        QualityPreset preset = QualityPreset.ForLevel(level)!;

        Assert.Equal(level, example.GetInt("level-change"));
        foreach (var (name, value) in preset.Settings)
            Assert.True(example.Get(name) == value, $"{file}: {name} is '{example.Get(name)}', the {preset.Name} preset has '{value}'");
    }

    [Fact]
    public void Choosing_a_preset_keeps_the_connection_settings()
    {
        var settings = SettingsDocument.FromText("x.ini", "level-change = -2\ndownload-workers = 160\ndds-build-workers = 16\n");

        QualityPreset.Ultra.ApplyTo(settings);

        Assert.Equal(0, settings.GetInt("level-change"));
        Assert.Equal(160, settings.GetInt("download-workers"));
        Assert.Equal(8, settings.GetInt("dds-build-workers"));
    }

    [Theory]
    [InlineData("Ultra_4096-4096", 0, 4096, 4096)]
    [InlineData("Balanced_1024-1024", -2, 1024, 1024)]
    [InlineData("level0_4096-4096", 0, 4096, 4096)]
    [InlineData("level-3_512-512", -3, 512, 512)]
    public void Setup_folders_are_named_by_their_preset_or_level(string name, int level, int zl16, int zl18) =>
        Assert.Equal(new SetupName(level, zl16, zl18), CacheInventory.ParseSetupName(name));

    [Theory]
    [InlineData("Unknown_4096-4096")]
    [InlineData("XPME_Europe")]
    public void Other_folders_are_not_setups(string name) => Assert.Null(CacheInventory.ParseSetupName(name));

    [Fact]
    public void Cache_folders_are_told_apart_by_their_names()
    {
        _tree.WriteText("cache/level-2_2048-1024/XPME_Europe/textures/a.dds", "12345");
        _tree.WriteText("cache/level+1_4096-4096/XPME_Europe/textures/b.dds", "1");
        _tree.WriteText("cache/.orthostreamxp-logs/orthostreamxp.log", "log");
        _tree.WriteText("cache/XPME_Europe/textures/old.dds", "12");

        IReadOnlyList<CacheEntry> entries = CacheInventory.Scan(_tree.PathOf("cache"), TestContext.Current.CancellationToken);

        Assert.Equal(["level-2_2048-1024", "level+1_4096-4096", ".orthostreamxp-logs", "XPME_Europe"], entries.Select(e => e.Name));
        Assert.Equal([CacheEntryKind.Setup, CacheEntryKind.Setup, CacheEntryKind.Logs, CacheEntryKind.Other], entries.Select(e => e.Kind));
        Assert.Equal(new SetupName(-2, 2048, 1024), entries[0].Setup);
        Assert.Equal((5, 1), (entries[0].Bytes, entries[0].Files));
        Assert.Equal("7.1 GB", CacheInventory.FormatBytes(7_623_566_950));
    }

    [Fact]
    public void A_folder_in_the_way_is_explained_with_a_way_out()
    {
        UserProblem problem = MountController.Explain(new SettingsException(
            @"Mount point path must not already exist; WinFsp creates it while mounted: C:\X-Plane 12\Custom Scenery\XPME_Europe (Custom Scenery already has a folder of that name: remove or rename it)"));

        Assert.Equal("A scenery folder is in the way", problem.Title);
        Assert.Contains("folder called XPME_Europe", problem.Message, StringComparison.Ordinal);
        Assert.Equal(ProblemAction.OpenCustomScenery, problem.Action);
        Assert.Equal(ProblemAction.RunSetup, MountController.Explain(new SettingsException("Setting required: cache")).Action);
    }

    [Fact]
    public void Packages_in_Custom_Scenery_are_explained_with_where_to_put_them()
    {
        UserProblem problem = MountController.Explain(new SettingsException(
            MountSettings.InCustomScenery + @": C:\X-Plane 12\Custom Scenery\z_autoortho\scenery"));

        Assert.Equal("Your scenery packages are in Custom Scenery", problem.Title);
        Assert.Equal(SystemCheck.InCustomSceneryExplanation, problem.Message);
        Assert.Equal(ProblemAction.OpenSettings, problem.Action);

        Directory.CreateDirectory(_tree.PathOf("X-Plane 12/Custom Scenery"));
        string xplane = _tree.PathOf("X-Plane 12");
        string inside = _tree.PathOf("X-Plane 12/Custom Scenery/z_autoortho/scenery");
        Assert.True(SystemCheck.IsInCustomScenery(inside, xplane));
        Assert.False(SystemCheck.IsInCustomScenery(_tree.PathOf("Base Packages"), xplane));
        Assert.False(SystemCheck.IsInCustomScenery(null, xplane));
        Assert.False(SystemCheck.IsInCustomScenery(" ", xplane));
        // Without a real X-Plane folder there is nothing to check against: the window asks for X-Plane first.
        Assert.False(SystemCheck.IsInCustomScenery(inside, null));
        Assert.False(SystemCheck.IsInCustomScenery(inside, _tree.PathOf("not X-Plane")));
    }

    [Fact]
    public void The_values_that_count_are_the_last_lines_and_empty_ones_are_not_set()
    {
        var settings = SettingsDocument.FromText("x.ini", "# a comment\nlevel-change = -2\nlevel-change = 1\nlog-output =\nseasons = on\n");
        Assert.Equal(new Dictionary<string, string> { ["level-change"] = "1", ["seasons"] = "on" }, settings.Values());
    }

    [Fact]
    public void Changed_settings_are_named_once_in_the_order_of_the_settings_page()
    {
        IReadOnlyList<string> names = SettingNames.Of(["season-strength", "level-change", "max-wait", "season", "build-memory"]);
        Assert.Equal(["Quality", "Seasons", "Tuned settings"], names);
        Assert.Equal("Quality, Seasons and Tuned settings", SettingNames.List(names));
        Assert.Equal("Quality", SettingNames.List(["Quality"]));
        // The Night tab comes after Colours and before Other.
        Assert.Equal(["Exposure", "Night lights", "Photos at night", "X-Plane address"],
            SettingNames.Of(["xplane-address", "skip-photos-sun-below", "skip-photos-at-night", "night-lights", "exposure"]));
    }

    [Fact]
    public void X_Plane_start_options_are_read_whole_and_never_need_a_restart()
    {
        // A value that starts with dashes, and several options in one value.
        SettingValues values = TestSettings.Of("xplane-arguments = --allow_reshade --no_sound", "cache = C:\\c");
        Assert.Equal("--allow_reshade --no_sound", values.String("xplane-arguments"));
        Assert.Equal("C:\\c", values.String("cache"));

        Assert.Equal(["X-Plane start options"], SettingNames.Of(["xplane-arguments"]));
        Assert.Contains("xplane-arguments", SettingNames.NotForStreaming);
    }

    [Fact]
    public void Log_lines_are_split_for_the_log_page()
    {
        LogLine line = LogLine.Parse("2026-10-25 21:05:58,742 WARNING Downloads are slow");
        Assert.Equal(("21:05:58", "WARNING", "Downloads are slow", true), (line.Time, line.Level, line.Text, line.IsProblem));
        Assert.Equal("odd line", LogLine.Parse("odd line").Text);
    }

    [Fact]
    public void The_setup_suggests_the_cache_beside_the_base_packages()
    {
        // OrthoStreamXP\Base Packages and OrthoStreamXP\Cache in one folder.
        Assert.Equal(@"D:\OrthoStreamXP\Cache", SystemCheck.SuggestedCacheFolder(@"D:\OrthoStreamXP\Base Packages"));
        Assert.Equal(@"D:\OrthoStreamXP\Cache", SystemCheck.SuggestedCacheFolder(@"D:\OrthoStreamXP\Base Packages\"));

        // Nothing chosen yet, a drive's root or no full path: OrthoStreamXP\Cache in the home folder.
        string home = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OrthoStreamXP", "Cache");
        Assert.Equal(home, SystemCheck.SuggestedCacheFolder(null));
        Assert.Equal(home, SystemCheck.SuggestedCacheFolder(@"D:\"));
        Assert.Equal(home, SystemCheck.SuggestedCacheFolder("Base Packages"));
    }
}
