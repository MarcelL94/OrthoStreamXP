using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Terrain (.ter) definitions whose LOAD_CENTER pixel size matches the texture size, served from memory:
/// adjusted from the source package's file, or generated from a tile file name.
/// Each answer is remembered for the session, so a .ter is produced once however often X-Plane opens it.
/// </summary>
internal sealed partial class TerrainDefinitions(SceneryRoots roots)
{
    private const string WaterOverlaySuffix = "_water_overlay.ter", SeaOverlaySuffix = "_sea_overlay.ter";
    private const string DecalLibrary = "lib/g10/decals/maquify_2_green_key.dcl";
    /// <summary>Provider code of the per-tile sea masks, e.g. 11952_36192_ZL16.png beside 11952_36192_BI16.dds.</summary>
    private const string SeaMaskCode = "ZL";

    private readonly ConcurrentDictionary<string, byte[]?> _resolved = new();

    [GeneratedRegex(@"^(\s*LOAD_CENTER\s+[-+\d.eE]+\s+[-+\d.eE]+\s+[-+\d.eE]+\s+)\d+(\s*(?:#.*)?)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex LoadCenterPattern();

    /// <summary>
    /// The photo texture stem a .ter file name stands for: 11952_36192_BI16.ter, ..._water_overlay.ter and
    /// ..._sea_overlay.ter all use 11952_36192_BI16.dds. Null for names that are not tiles.
    /// </summary>
    public static TextureName? TextureOf(string terrainFileName, out bool isWaterOverlay, out bool isSeaOverlay)
    {
        isWaterOverlay = terrainFileName.EndsWith(WaterOverlaySuffix, StringComparison.OrdinalIgnoreCase);
        isSeaOverlay = terrainFileName.EndsWith(SeaOverlaySuffix, StringComparison.OrdinalIgnoreCase);
        string stem = isWaterOverlay ? terrainFileName[..^WaterOverlaySuffix.Length]
            : isSeaOverlay ? terrainFileName[..^SeaOverlaySuffix.Length]
            : Path.GetExtension(terrainFileName).Equals(".ter", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(terrainFileName)
            : "";
        return TextureName.TryParseStem(stem, out TextureName name) ? name : null;
    }

    /// <summary>The definition (UTF-8) for a terrain/*.ter path, or null when this is not one we can provide.</summary>
    public byte[]? Resolve(VirtualPath path) => path.IsTerrain ? _resolved.GetOrAdd(path.Key, _ => Produce(path)) : null;

    private byte[]? Produce(VirtualPath path)
    {
        string? original = SceneryRoots.Find(roots.Source, path.Parts);
        if (original is not null && !File.Exists(original)) return null;
        string? content = original is not null ? Adjusted(path, original) : Generated(path);
        return content is null ? null : Encoding.UTF8.GetBytes(content);
    }

    /// <summary>
    /// Deletes the definitions earlier versions wrote into a setup folder's terrain\ (they are served from memory now).
    /// Only tile names were ever written there, each standing in for the package's file of the same name.
    /// </summary>
    public static void RemoveCachedCopies(string cache)
    {
        string folder = Path.Join(cache, "terrain");
        if (!Directory.Exists(folder)) return;
        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(folder, "*.ter"))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // left for the next start
            }
        }
        if (removed > 0) Log.Info($"Removed {removed} terrain definitions from {folder}: they are served from memory now");
    }

    /// <summary>
    /// The source definition with its LOAD_CENTER pixel size set to its texture's size (LOAD_CENTER_BORDER is kept).
    /// A definition without LOAD_CENTER gets one after its TERRAIN line: without it X-Plane loads the texture once,
    /// at full size, and never reads it again as the distance changes.
    /// </summary>
    private string? Adjusted(VirtualPath path, string original)
    {
        // Not a tile name: its texture is not built here, so the definition is served as it is.
        if (TextureOf(path.Name, out _, out _) is not { } name) return null;
        int size = roots.Textures.SizeFor(name.Zoom);
        string content = File.ReadAllText(original, Encoding.UTF8).ReplaceLineEndings("\n");
        int count = 0;
        string updated = LoadCenterPattern().Replace(content, m =>
        {
            count++;
            return m.Groups[1].Value + size.ToString(CultureInfo.InvariantCulture) + m.Groups[2].Value;
        });
        if (count > 0) return updated;
        Match terrain = TerrainLinePattern().Match(content);
        if (!terrain.Success) return null;
        Log.Detail($"{path.Display} has no LOAD_CENTER; one is added so X-Plane loads its texture by distance");
        return content.Insert(terrain.Index + terrain.Length, $"\nLOAD_CENTER {LoadCenter(name)} {size.ToString(CultureInfo.InvariantCulture)}");
    }

    [GeneratedRegex(@"^[ \t]*TERRAIN[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex TerrainLinePattern();

    /// <summary>"lat lon size_m" of a texture's LOAD_CENTER: its centre and its width on the ground.</summary>
    private static string LoadCenter(TextureName name)
    {
        var (latitude, longitude) = name.Center;
        return string.Create(CultureInfo.InvariantCulture, $"{latitude:F5} {longitude:F5} {(int)name.GroundSizeM}");
    }

    /// <summary>
    /// Builds a definition from its file name, in one of three forms:
    /// <list type="bullet">
    /// <item>ROW_COL_BI16.ter: the photo texture with the standard decal library;</item>
    /// <item>..._water_overlay.ter: inland water blended with the shared water_transition.png;</item>
    /// <item>..._sea_overlay.ter: sea blended with the tile's own ROW_COL_ZL16.png coastline mask, which must
    /// exist in the package (masks come from coastline data and cannot be made from imagery).</item>
    /// </list>
    /// </summary>
    private string? Generated(VirtualPath path)
    {
        if (path.Parts.Length != 2) return null;
        if (TextureOf(path.Name, out bool isWaterOverlay, out bool isSeaOverlay) is not { } name) return null;
        string stem = name.Stem;
        string center = LoadCenter(name);
        var definition = new StringBuilder()
            .Append("A\n800\nTERRAIN\n\n")
            .Append(CultureInfo.InvariantCulture, $"LOAD_CENTER {center} {roots.Textures.SizeFor(name.Zoom)}\n")
            .Append(CultureInfo.InvariantCulture, $"BASE_TEX_NOWRAP ../textures/{stem}.dds\n");

        if (isSeaOverlay)
        {
            string maskName = (name with { Code = SeaMaskCode }).Stem + ".png";
            if (roots.FindTexture(VirtualPath.Of("textures", maskName)) is { } mask && PngWidth(mask) is { } maskSize)
            {
                return definition
                    .Append(CultureInfo.InvariantCulture, $"LOAD_CENTER_BORDER {center} {maskSize}\n")
                    .Append(CultureInfo.InvariantCulture, $"BORDER_TEX ../textures/{maskName}\n")
                    .Append("WET\nNO_SHADOW\n")
                    .ToString();
            }
            // A missing .ter makes X-Plane reject the whole DSF (and flag the package as broken), so fall
            // back to the generic water blend rather than leaving the overlay out.
            Log.Warning($"Sea mask textures/{maskName} is missing or not a PNG; {path.Display} uses water_transition.png instead");
        }
        if (isWaterOverlay || isSeaOverlay)
            return definition.Append("BORDER_TEX ../textures/water_transition.png\nWET\nNO_SHADOW\n").ToString();
        return definition.Append(CultureInfo.InvariantCulture, $"DECAL_LIB {DecalLibrary}\n").Append("NO_ALPHA\n").ToString();
    }

    /// <summary>The width of a PNG, read from its IHDR chunk, or null when the file is not a PNG.</summary>
    internal static int? PngWidth(string file)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Span<byte> header = stackalloc byte[24];
        try
        {
            using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (RandomAccess.Read(handle, header, 0) < 24 || !header[..8].SequenceEqual(signature)) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return BinaryPrimitives.ReadInt32BigEndian(header[16..]);
    }
}
