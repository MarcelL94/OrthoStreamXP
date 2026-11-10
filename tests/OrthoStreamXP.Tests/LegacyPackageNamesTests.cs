using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Tests;

/// <summary>The regions 1.1.1 installed as OSXP_* take the OrthoStream_* names, with their textures and scenery_packs.ini entries.</summary>
public sealed class LegacyPackageNamesTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string Packages => _tree.PathOf("packages");

    private string Cache => _tree.PathOf("cache");

    private string XPlane => _tree.PathOf("X-Plane 12");

    private string SceneryPacksFile => _tree.PathOf("X-Plane 12/Custom Scenery/scenery_packs.ini");

    private void InstallOld(string package)
    {
        _tree.WriteText($"packages/{package}/Earth nav data/+40+000/+45+005.dsf", "dsf");
        _tree.WriteText($"packages/{package}/{RegionInstaller.MarkerName}", """{"Id":"eur","Release":"1","Overlays":true,"InstalledUtc":"2026-11-10T00:00:00Z"}""");
    }

    [Fact]
    public void The_regions_their_textures_and_their_scenery_packs_entries_take_the_new_names()
    {
        InstallOld("OSXP_Eur");
        _tree.WriteText("packages/OSXP_Overlays/Earth nav data/+40+000/+45+005.dsf", "overlay");
        _tree.WriteText($"packages/OSXP_Overlays/{OverlayPackage.RecordName}", "{}");
        _tree.WriteText("packages/.osxp-download/afr/1/state.json", "{}");
        _tree.WriteText("cache/Ultra_4096-4096/OSXP_Eur/textures/x.dds", "dds");
        _tree.WriteText("cache/Sharp_2048-2048/OSXP_Eur/textures/y.dds", "dds");
        _tree.WriteText("X-Plane 12/Custom Scenery/scenery_packs.ini", string.Join("\r\n",
            "I", "1000 Version", "SCENERY",
            "SCENERY_PACK Custom Scenery/KSEA Demo Area/",
            "SCENERY_PACK_DISABLED Custom Scenery/OSXP_Overlays/",
            "SCENERY_PACK Custom Scenery/z_osxp_eur/",
            "SCENERY_PACK Custom Scenery/OSXP_Eur/") + "\r\n");

        LegacyPackageNames.Move(Packages, Cache, XPlane);

        Assert.Equal(["OrthoStream_Eur", "OrthoStream_Overlays"], SceneryPacks.FindPackages(Packages, out _).Select(Path.GetFileName));
        Assert.True(File.Exists(_tree.PathOf($"packages/OrthoStream_Overlays/{OverlayPackage.RecordName}")));
        Assert.True(File.Exists(_tree.PathOf("packages/.orthostream-download/afr/1/state.json")));
        Assert.False(Directory.Exists(_tree.PathOf("packages/.osxp-download")));
        Assert.True(File.Exists(_tree.PathOf("cache/Ultra_4096-4096/OrthoStream_Eur/textures/x.dds")));
        Assert.True(File.Exists(_tree.PathOf("cache/Sharp_2048-2048/OrthoStream_Eur/textures/y.dds")));
        Assert.Equal(string.Join("\r\n",
            "I", "1000 Version", "SCENERY",
            "SCENERY_PACK Custom Scenery/KSEA Demo Area/",
            "SCENERY_PACK_DISABLED Custom Scenery/OrthoStream_Overlays/", // still disabled, still in its place
            "SCENERY_PACK Custom Scenery/z_osxp_eur/",                     // OrthoStudio XP's own: untouched
            "SCENERY_PACK Custom Scenery/OrthoStream_Eur/") + "\r\n", File.ReadAllText(SceneryPacksFile));
        Assert.True(File.Exists(SceneryPacksFile + ".bak"));
        using var http = new HttpClient();
        Assert.Equal("1", new RegionInstaller(new AssetDownload(http), Packages).Installed()["eur"].Release);
    }

    [Fact]
    public void Running_again_changes_nothing()
    {
        InstallOld("OSXP_Eur");
        _tree.WriteText("X-Plane 12/Custom Scenery/scenery_packs.ini", "I\n1000 Version\nSCENERY\nSCENERY_PACK Custom Scenery/OSXP_Eur/\n");
        LegacyPackageNames.Move(Packages, Cache, XPlane);
        File.Delete(SceneryPacksFile + ".bak");

        LegacyPackageNames.Move(Packages, Cache, XPlane);

        Assert.Equal("I\n1000 Version\nSCENERY\nSCENERY_PACK Custom Scenery/OrthoStream_Eur/\n", File.ReadAllText(SceneryPacksFile));
        Assert.False(File.Exists(SceneryPacksFile + ".bak"));
    }

    [Fact]
    public void A_folder_OrthoStreamXP_did_not_install_keeps_its_name()
    {
        _tree.WriteText("packages/OSXP_Eur/Earth nav data/+40+000/+45+005.dsf", "someone else's");
        _tree.WriteText("X-Plane 12/Custom Scenery/scenery_packs.ini", "I\n1000 Version\nSCENERY\nSCENERY_PACK Custom Scenery/OSXP_Eur/\n");

        LegacyPackageNames.Move(Packages, Cache, XPlane);

        Assert.True(Directory.Exists(_tree.PathOf("packages/OSXP_Eur")));
        Assert.False(Directory.Exists(_tree.PathOf("packages/OrthoStream_Eur")));
        Assert.Contains("Custom Scenery/OSXP_Eur/", File.ReadAllText(SceneryPacksFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_renamed_over_a_folder_of_the_new_name()
    {
        InstallOld("OSXP_Eur");
        _tree.WriteText("packages/OrthoStream_Eur/Earth nav data/+50+010/+51+011.dsf", "new");

        IReadOnlyList<string> left = LegacyPackageNames.MovePackages(Packages);

        Assert.Equal(["OSXP_Eur"], left);
        Assert.True(File.Exists(_tree.PathOf("packages/OSXP_Eur/Earth nav data/+40+000/+45+005.dsf")));
        Assert.Equal("new", File.ReadAllText(_tree.PathOf("packages/OrthoStream_Eur/Earth nav data/+50+010/+51+011.dsf")));
    }

    [Fact]
    public void A_region_left_under_its_old_name_shows_as_installed_but_is_not_installed_or_deleted_beside_it()
    {
        InstallOld("OSXP_Eur");
        _tree.WriteText("packages/OrthoStream_Eur/stray.txt", "in the way");
        using var http = new HttpClient();
        var installer = new RegionInstaller(new AssetDownload(http), Packages);

        Assert.Equal("1", installer.Installed()["eur"].Release); // not offered for download again

        InstallException error = Assert.Throws<InstallException>(() => installer.Delete("eur"));
        Assert.Contains("OSXP_Eur", error.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(_tree.PathOf("packages/OSXP_Eur")));
    }

    [Fact]
    public void An_entry_already_listed_under_its_new_name_is_not_doubled()
    {
        List<string> lines = SceneryPacks.Rename(
            ["SCENERY_PACK Custom Scenery/OrthoStream_Eur/", "SCENERY_PACK_DISABLED Custom Scenery/OSXP_Eur/"],
            [("OSXP_Eur", "OrthoStream_Eur")]);

        Assert.Equal(["SCENERY_PACK Custom Scenery/OrthoStream_Eur/"], lines);
    }
}
