using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Tests;

public sealed class SceneryPacksTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string CustomScenery => _tree.PathOf("X-Plane 12/Custom Scenery");

    private void Package(string relative) => Directory.CreateDirectory(_tree.PathOf(relative + "/Earth nav data"));

    [Fact]
    public void The_packages_move_to_the_end_and_a_disabled_one_stays_disabled()
    {
        _tree.WriteText("X-Plane 12/Custom Scenery/scenery_packs.ini", """
            I
            1000 Version
            SCENERY

            SCENERY_PACK Custom Scenery/XPME_Europe/
            SCENERY_PACK Custom Scenery/EDDB Berlin/
            SCENERY_PACK *GLOBAL_AIRPORTS*
            SCENERY_PACK_DISABLED Custom Scenery\XPME_Africa
            SCENERY_PACK Custom Scenery/z_mesh/

            """.ReplaceLineEndings("\r\n")); // CRLF however git checked this file out

        Assert.True(SceneryPacks.MoveToEnd(CustomScenery, ["XPME_Africa", "XPME_Europe", "XPME_Asia"]));

        string file = Path.Join(CustomScenery, "scenery_packs.ini");
        Assert.Equal("""
            I
            1000 Version
            SCENERY

            SCENERY_PACK Custom Scenery/EDDB Berlin/
            SCENERY_PACK *GLOBAL_AIRPORTS*
            SCENERY_PACK Custom Scenery/z_mesh/
            SCENERY_PACK_DISABLED Custom Scenery/XPME_Africa/
            SCENERY_PACK Custom Scenery/XPME_Europe/
            SCENERY_PACK Custom Scenery/XPME_Asia/

            """.ReplaceLineEndings("\r\n"), File.ReadAllText(file));
        Assert.Contains("SCENERY_PACK Custom Scenery/XPME_Europe/\r\nSCENERY_PACK Custom Scenery/EDDB Berlin/", File.ReadAllText(file + ".bak"), StringComparison.Ordinal);
        Assert.False(SceneryPacks.MoveToEnd(CustomScenery, ["XPME_Africa", "XPME_Europe", "XPME_Asia"]), "already in place: left alone");
    }

    [Fact]
    public void An_entry_with_the_full_path_is_recognised()
    {
        string fullPath = Path.Join(CustomScenery, "XPME_Europe").Replace('\\', '/') + "/";
        List<string> lines = SceneryPacks.Reorder(["SCENERY", "", $"SCENERY_PACK {fullPath}", "SCENERY_PACK Custom Scenery/KSEA/"], ["XPME_Europe"]);
        Assert.Equal(["SCENERY", "", "SCENERY_PACK Custom Scenery/KSEA/", "SCENERY_PACK Custom Scenery/XPME_Europe/"], lines);
    }

    [Fact]
    public void An_entry_for_a_package_of_the_same_name_elsewhere_is_replaced()
    {
        List<string> lines = SceneryPacks.Reorder(
            [
                "SCENERY", "",
                @"SCENERY_PACK C:\OrthoData\XPME\Base Packages\XPME_Africa/",
                "SCENERY_PACK Custom Scenery/KSEA/",
                @"SCENERY_PACK D:\Other\XPME_Asia",
                @"SCENERY_PACK C:\OrthoData\XPME\Base Packages\XPME_Africa_Extra/",
            ],
            ["XPME_Africa", "XPME_Asia"]);
        Assert.Equal(
            [
                "SCENERY", "",
                "SCENERY_PACK Custom Scenery/KSEA/",
                @"SCENERY_PACK C:\OrthoData\XPME\Base Packages\XPME_Africa_Extra/",
                "SCENERY_PACK Custom Scenery/XPME_Africa/",
                "SCENERY_PACK Custom Scenery/XPME_Asia/",
            ], lines);
    }

    [Fact]
    public void An_overlays_package_goes_above_the_other_packages()
    {
        List<string> lines = SceneryPacks.Reorder(
            ["SCENERY", "", "SCENERY_PACK Custom Scenery/KSEA/", "SCENERY_PACK_DISABLED Custom Scenery/XPME_Overlays/"],
            ["XPME_Africa", "XPME_Asia", "XPME_Overlays", "XPME_South_America"]);
        Assert.Equal(
            [
                "SCENERY", "", "SCENERY_PACK Custom Scenery/KSEA/",
                "SCENERY_PACK_DISABLED Custom Scenery/XPME_Overlays/",
                "SCENERY_PACK Custom Scenery/XPME_Africa/",
                "SCENERY_PACK Custom Scenery/XPME_Asia/",
                "SCENERY_PACK Custom Scenery/XPME_South_America/",
            ], lines);
    }

    [Fact]
    public void A_missing_file_is_created()
    {
        Directory.CreateDirectory(CustomScenery);
        Assert.True(SceneryPacks.MoveToEnd(CustomScenery, ["XPME_Europe"]));
        Assert.Equal("I\r\n1000 Version\r\nSCENERY\r\nSCENERY_PACK Custom Scenery/XPME_Europe/\r\n", File.ReadAllText(Path.Join(CustomScenery, "scenery_packs.ini")));
    }

    [Fact]
    public void Every_package_in_the_base_folder_is_planned_with_its_own_cache_and_mount()
    {
        Directory.CreateDirectory(CustomScenery);
        Package("BP/XPME_Europe");
        Package("BP/XPME_Africa");
        Directory.CreateDirectory(_tree.PathOf("BP/downloads"));
        SettingValues args = TestSettings.Of($"base-packages = {_tree.PathOf("BP")}\\", $"xplane = {_tree.PathOf("X-Plane 12")}", "cache = unused");

        var plan = MountSettings.PlanPackages(args, _tree.PathOf("cache"), out string? customScenery, out var notPackages);

        Assert.Equal(CustomScenery, customScenery);
        Assert.Equal([_tree.PathOf("BP/downloads")], notPackages);
        Assert.Equal(
            [
                new PackagePlan("XPME_Africa", _tree.PathOf("BP/XPME_Africa"), _tree.PathOf("cache/XPME_Africa"), Path.Join(CustomScenery, "XPME_Africa")),
                new PackagePlan("XPME_Europe", _tree.PathOf("BP/XPME_Europe"), _tree.PathOf("cache/XPME_Europe"), Path.Join(CustomScenery, "XPME_Europe")),
            ], plan);
    }

    [Fact]
    public void One_source_with_the_xplane_folder_mounts_under_its_own_name_and_keeps_the_cache()
    {
        Directory.CreateDirectory(CustomScenery);
        Package("BP/XPME_Europe");
        SettingValues args = TestSettings.Of($"source = {_tree.PathOf("BP/XPME_Europe")}", $"xplane = {_tree.PathOf("X-Plane 12")}", "cache = unused");

        var plan = MountSettings.PlanPackages(args, _tree.PathOf("cache"), out _, out _);

        Assert.Equal([new PackagePlan("XPME_Europe", _tree.PathOf("BP/XPME_Europe"), _tree.PathOf("cache"), Path.Join(CustomScenery, "XPME_Europe"))], plan);
    }

    [Theory]
    [InlineData("--base-packages BP --mountpoint M", "give xplane instead of mountpoint")]
    [InlineData("--base-packages BP", "give either xplane")]
    [InlineData("--xplane X-Plane", "give either source")]
    [InlineData("--source BP/XPME_Europe --base-packages BP --xplane X-Plane", "give either source")]
    [InlineData("--base-packages BP --xplane BP", "has no Custom Scenery folder")]
    [InlineData("--base-packages BP --xplane X-Plane", "Custom Scenery already has a folder of that name")]
    [InlineData("--base-packages X-Plane/Custom~Scenery/z_autoortho/scenery --xplane X-Plane", "must not be in X-Plane's Custom Scenery")]
    [InlineData("--source X-Plane/Custom~Scenery/XPME_Europe --xplane X-Plane", "must not be in X-Plane's Custom Scenery")]
    public void Mistakes_are_explained(string arguments, string message)
    {
        Directory.CreateDirectory(_tree.PathOf("X-Plane/Custom Scenery/XPME_Europe"));
        Package("BP/XPME_Europe");
        string[] lines = [.. arguments.Split(' ').Chunk(2).Select(pair => $"{pair[0][2..]} = {_tree.PathOf(pair[1].Replace('~', ' '))}")];

        var error = Assert.Throws<SettingsException>(() => MountSettings.PlanPackages(TestSettings.Of(lines), _tree.PathOf("cache"), out _, out _));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_own_folder_named_Custom_Scenery_outside_X_Plane_can_hold_the_packages()
    {
        Directory.CreateDirectory(CustomScenery);
        Package("Mine/Custom Scenery/XPME_Europe");
        SettingValues args = TestSettings.Of($"base-packages = {_tree.PathOf("Mine/Custom Scenery")}", $"xplane = {_tree.PathOf("X-Plane 12")}", "cache = unused");

        var plan = MountSettings.PlanPackages(args, _tree.PathOf("cache"), out _, out _);

        Assert.Equal(["XPME_Europe"], plan.Select(p => p.Name));
    }

    [Theory]
    [InlineData(@"C:\X-Plane 12\Custom Scenery", true)]
    [InlineData(@"C:\X-Plane 12\Custom Scenery\", true)]
    [InlineData(@"C:\X-Plane 12\custom scenery\z_autoOrtho\scenery", true)]
    [InlineData(@"D:\OrthoStreamXP\Base Packages", false)]
    [InlineData(@"D:\OrthoStreamXP\Custom Scenery\XPME", false)]
    [InlineData(@"C:\X-Plane 12\Custom Scenery Backup\XPME", false)]
    [InlineData(@"C:\X-Plane 12", false)]
    [InlineData(@"D:\X-Plane 12\Custom Scenery\z_autoOrtho\scenery", false)]
    public void Only_the_chosen_X_Planes_Custom_Scenery_counts(string folder, bool inCustomScenery) =>
        Assert.Equal(inCustomScenery, SceneryPacks.IsInCustomScenery(folder, @"C:\X-Plane 12\"));
}
