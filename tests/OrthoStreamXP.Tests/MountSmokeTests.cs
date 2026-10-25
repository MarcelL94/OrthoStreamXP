using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>
/// A real WinFsp mount with the synthetic imagery (no internet, no X-Plane): a texture and a terrain definition read
/// through Windows, then a clean unmount. Skipped where WinFsp is not installed; CI installs it.
/// </summary>
public sealed class MountSmokeTests : IDisposable
{
    private const string Stem = "21904_35824_BI16";

    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void A_real_mount_serves_a_texture_and_a_terrain_definition_then_unmounts()
    {
        // On CI (GitHub Actions sets CI) WinFsp is installed for this test, so it must run there, never skip.
        if (Environment.GetEnvironmentVariable("CI") is null) Assert.SkipUnless(SystemCheck.WinFspInstalled(), "WinFsp is not installed");
        Directory.CreateDirectory(_tree.PathOf("source/Earth nav data"));
        string mount = _tree.PathOf("mount");
        MountSettings settings = MountSettings.FromValues(TestSettings.Of(
            $"source = {_tree.PathOf("source")}", $"cache = {_tree.PathOf("cache")}", $"mountpoint = {mount}",
            "texture-provider = synthetic", "level-change = -3"));

        // Dispose stops without the flight summaries, so nothing is written to the user's history.
        using (MountSession.Start(settings))
        {
            byte[] dds = File.ReadAllBytes(Path.Join(mount, "textures", Stem + ".dds"));
            Assert.Equal("DDS "u8.ToArray(), dds[..4]);
            Assert.Equal(DdsLayout.For(512).Length, dds.Length); // level -3: a ZL16 texture is 512 px

            string definition = File.ReadAllText(Path.Join(mount, "terrain", Stem + ".ter"));
            Assert.Contains($"BASE_TEX_NOWRAP ../textures/{Stem}.dds", definition, StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(mount)); // the mount point goes with the mount
    }
}
