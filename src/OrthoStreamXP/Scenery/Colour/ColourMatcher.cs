using System.Collections.Concurrent;
using System.Text.Json;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>Where a texture's picture comes from at the moment: the photo, or the low-zoom base (stand-ins before the photo arrives).</summary>
internal enum PictureSource
{
    Base,
    Photo,
}

/// <summary>
/// Each texture's colour correction for the session (<see cref="ColourMatch"/>), kept per version of its picture, and
/// the relation of photo to base usual in each 1° area. That relation is learnt from the textures measured there (the
/// median of their own) and kept in the log folder; until an area has <see cref="LearntFrom"/> of them it is
/// <see cref="ColourMatch.DefaultTarget"/>. An area's relation is fixed for the session at its first use, so the
/// textures of a flight never change colour under it.
/// </summary>
internal sealed class ColourMatcher
{
    public const string FileName = "colour-match.json";
    /// <summary>Textures measured before an area's own relation is used.</summary>
    public const int LearntFrom = 12;
    private const int MaxTextures = 20_000;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly Func<TextureName, (byte[] Rgb, int Size)?> _base;
    private readonly Func<byte[], int, int, byte[]> _resize;
    private readonly string? _path;
    private readonly float _strength;
    private readonly ConcurrentDictionary<string, (object Version, TextureColour? Colour)> _textures = new();
    private readonly ConcurrentDictionary<AreaName, ColourGain> _targets = new();
    private readonly Lock _gate = new();
    /// <summary>By area, each measured texture's relation to the base (the median of its cells), as R, G, B.</summary>
    private readonly Dictionary<string, Dictionary<string, float[]>> _measured;
    private bool _changed;

    /// <param name="baseRegion">A texture's area from the low-zoom base, or null when it is not on this PC yet.</param>
    /// <param name="resize">Resizes a square RGB picture (picture, its size, the size wanted).</param>
    /// <param name="path">The file the areas' relations are kept in, or null for none.</param>
    /// <param name="strength">How much of each correction is made, 0 to 1.</param>
    public ColourMatcher(Func<TextureName, (byte[] Rgb, int Size)?> baseRegion, Func<byte[], int, int, byte[]> resize, string? path, double strength = 1)
    {
        _base = baseRegion;
        _resize = resize;
        _path = path;
        _strength = (float)Math.Clamp(strength, 0, 1);
        _measured = Load(path);
    }

    /// <summary>
    /// The correction of a texture whose picture is the photo, or null when it changes nothing or its base tile is not
    /// here yet (asked again then).
    /// </summary>
    /// <param name="version">Changes when the photo does (a finer level from another capture): the correction is measured again.</param>
    /// <param name="sample">The photo at about the size asked, RGB, or null when there is none.</param>
    public TextureColour? ForPhoto(TextureName name, object version, Func<int, (byte[] Rgb, int Size)?> sample)
    {
        string key = name.Stem;
        if (_textures.TryGetValue(key, out var known) && known.Version.Equals(version)) return known.Colour;
        int cells = ColourMatch.CellsAcross(name), size = cells * ColourMatch.SamplePerCell;
        if (_base(name) is not { } reference || sample(size) is not { } photo) return null;
        ColourGain?[] relation = ColourMatch.Relation(_resize(photo.Rgb, photo.Size, size), _resize(reference.Rgb, reference.Size, size), size, cells);
        AreaName area = AreaName.Of(name);
        TextureColour? colour = TextureColour.Matching(relation, cells, TargetOf(area), _strength);
        Note(area, key, relation);
        return Keep(key, version, colour);
    }

    /// <summary>The correction of a texture whose picture is the low-zoom base: its area's usual relation, so it looks like the photo will.</summary>
    public TextureColour? ForBase(TextureName name)
    {
        string key = name.Stem;
        if (_textures.TryGetValue(key, out var known) && known.Version is PictureSource.Base) return known.Colour;
        return Keep(key, PictureSource.Base, TextureColour.Uniform(TargetOf(AreaName.Of(name)) * _strength));
    }

    /// <summary>An area's usual relation of photo to base, fixed for the session at its first use.</summary>
    public ColourGain TargetOf(AreaName area) => _targets.GetOrAdd(area, a =>
    {
        lock (_gate)
        {
            if (!_measured.TryGetValue(a.ToString(), out var textures) || textures.Count < LearntFrom) return ColourMatch.DefaultTarget;
            return new ColourGain(Median(textures.Values, 0), Median(textures.Values, 1), Median(textures.Values, 2));
        }
    });

    /// <summary>Saves the areas' relations when they changed.</summary>
    public void Save()
    {
        if (_path is null) return;
        string json;
        lock (_gate)
        {
            if (!_changed) return;
            json = JsonSerializer.Serialize(_measured, Indented);
            _changed = false;
        }
        try
        {
            AtomicFile.Write(_path, System.Text.Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save {_path}: {ex.Message}");
        }
    }

    private void Note(AreaName area, string texture, ColourGain?[] relation)
    {
        ColourGain[] known = [.. relation.OfType<ColourGain>()];
        if (known.Length == 0) return;
        float[] median = [Median(known.Select(g => new[] { g.R, g.G, g.B }), 0), Median(known.Select(g => new[] { g.R, g.G, g.B }), 1),
            Median(known.Select(g => new[] { g.R, g.G, g.B }), 2)];
        lock (_gate)
        {
            string name = area.ToString();
            if (!_measured.TryGetValue(name, out var textures)) _measured[name] = textures = [];
            textures[texture] = median;
            _changed = true;
        }
    }

    private TextureColour? Keep(string key, object version, TextureColour? colour)
    {
        if (_textures.Count >= MaxTextures) _textures.Clear();
        _textures[key] = (version, colour);
        return colour;
    }

    private static float Median(IEnumerable<float[]> values, int channel)
    {
        float[] sorted = [.. values.Select(v => v[channel]).Order()];
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static Dictionary<string, Dictionary<string, float[]>> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float[]>>>(File.ReadAllText(path)) is { } data)
                return data;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not read {path}: {ex.Message}; the areas' colours are learnt again");
        }
        return [];
    }
}
