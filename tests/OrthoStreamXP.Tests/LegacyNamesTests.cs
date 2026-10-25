using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Tests;

/// <summary>What earlier versions left beside the program or in the cache moves to the app's folders, once and without overwriting.</summary>
public sealed class LegacyNamesTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string Logs => _tree.PathOf("appdata/logs");

    private string History => _tree.PathOf("appdata/history");

    [Theory]
    [InlineData("photo-xp")]       // the first versions' name
    [InlineData("orthostreamxp")]  // renamed, but still beside the program
    public void The_settings_beside_the_program_move_to_the_apps_folder(string name)
    {
        _tree.WriteText($"program/{name}.ini", "level-change = 1");
        _tree.WriteText($"program/{name}.tuned.json", "{}");
        _tree.WriteText($"program/{name}.connection.json", "{}");

        LegacyNames.MoveSettings(_tree.PathOf("program"), _tree.PathOf("appdata/orthostreamxp.ini"));

        Assert.Equal("level-change = 1", File.ReadAllText(_tree.PathOf("appdata/orthostreamxp.ini")));
        Assert.True(File.Exists(_tree.PathOf("appdata/orthostreamxp.tuned.json")));
        Assert.True(File.Exists(_tree.PathOf("appdata/orthostreamxp.connection.json")));
        Assert.Empty(Directory.EnumerateFiles(_tree.PathOf("program")));
    }

    [Fact]
    public void Settings_already_in_the_apps_folder_are_never_overwritten()
    {
        _tree.WriteText("program/photo-xp.ini", "old");
        _tree.WriteText("appdata/orthostreamxp.ini", "new");

        LegacyNames.MoveSettings(_tree.PathOf("program"), _tree.PathOf("appdata/orthostreamxp.ini"));

        Assert.Equal("new", File.ReadAllText(_tree.PathOf("appdata/orthostreamxp.ini")));
        Assert.Equal("old", File.ReadAllText(_tree.PathOf("program/photo-xp.ini")));
    }

    [Fact]
    public void The_caches_logs_and_history_move_to_the_apps_folders_and_what_describes_the_cache_stays()
    {
        _tree.WriteText("cache/.photo-xp-logs/photo-xp.log", "log");
        _tree.WriteText("cache/.photo-xp-logs/photo-xp.log.1", "older log");
        _tree.WriteText("cache/.photo-xp-logs/reads-20261027-153542.csv", "reads");
        _tree.WriteText("cache/.photo-xp-logs/flight-report.json", "{}");
        _tree.WriteText("cache/.photo-xp-logs/flights/flight-20261026-100000.json", "{}");
        _tree.WriteText("cache/.photo-xp-logs/colour-match.json", "{}");
        _tree.WriteText("cache/.photo-xp-logs/area-usage.json", "{}");
        _tree.WriteText("cache/.photo-xp-logs/map/+51+017-1-v2.png", "png");
        _tree.WriteText("cache/.photo-xp-base/12/2241/1367.tile", "tile");
        _tree.WriteText("cache/.photo-xp-landcover/N51E017.lc", "lc");
        _tree.WriteText("cache/level+1_4096-4096/z_ao_eur/textures/x.dds", "dds");

        LegacyNames.MoveCache(_tree.PathOf("cache"), Logs, History);

        Assert.Equal("log", File.ReadAllText(Path.Join(Logs, "orthostreamxp.log")));
        Assert.True(File.Exists(Path.Join(Logs, "orthostreamxp.log.1")));
        Assert.True(File.Exists(Path.Join(Logs, "reads-20261027-153542.csv")));
        Assert.True(File.Exists(Path.Join(History, "flight-report.json")));
        Assert.True(File.Exists(Path.Join(History, "flights", "flight-20261026-100000.json")));
        Assert.True(File.Exists(Path.Join(History, "colour-match.json")));
        // The cache's own: renamed folders, and what describes it stays in it.
        Assert.True(File.Exists(_tree.PathOf("cache/.orthostreamxp-logs/area-usage.json")));
        Assert.True(File.Exists(_tree.PathOf("cache/.orthostreamxp-logs/map/+51+017-1-v2.png")));
        Assert.True(File.Exists(_tree.PathOf("cache/.orthostreamxp-base/12/2241/1367.tile")));
        Assert.True(File.Exists(_tree.PathOf("cache/.orthostreamxp-landcover/N51E017.lc")));
        Assert.True(File.Exists(_tree.PathOf("cache/Extra_4096-4096/z_ao_eur/textures/x.dds")));
        Assert.False(Directory.Exists(_tree.PathOf("cache/.photo-xp-logs")));
    }

    [Fact]
    public void Level_folders_of_a_preset_take_its_name_and_others_keep_theirs()
    {
        _tree.WriteText("cache/level0_4096-4096/P/textures/a.dds", "ultra");
        _tree.WriteText("cache/level-2_1024-1024/P/textures/b.dds", "old balanced");
        _tree.WriteText("cache/Balanced_1024-1024/P/textures/b.dds", "balanced");
        _tree.WriteText("cache/level-3_512-512/P/textures/c.dds", "custom");

        LegacyNames.MoveCache(_tree.PathOf("cache"), Logs, History);

        Assert.Equal("ultra", File.ReadAllText(_tree.PathOf("cache/Ultra_4096-4096/P/textures/a.dds")));
        Assert.False(Directory.Exists(_tree.PathOf("cache/level0_4096-4096")));
        Assert.Equal("balanced", File.ReadAllText(_tree.PathOf("cache/Balanced_1024-1024/P/textures/b.dds"))); // never moved over
        Assert.True(Directory.Exists(_tree.PathOf("cache/level-2_1024-1024")));
        Assert.True(Directory.Exists(_tree.PathOf("cache/level-3_512-512")));
    }
}
