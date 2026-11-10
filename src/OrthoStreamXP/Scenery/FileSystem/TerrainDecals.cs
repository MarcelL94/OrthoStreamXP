using System.Collections.Concurrent;
using SkiaSharp;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// X-Plane's detail decals on the photo terrain, at a chosen strength. A tile's terrain definition names a decal
/// library (DECAL_LIB lib/g10/decals/....dcl) that sprinkles procedural grass, shrubs and dirt over the ground up
/// close; over sharp aerial photos it looks like noise. The decals modulate the ground's colours around mid-grey
/// (2x modulation: 128 leaves it as it is), so a decal texture faded towards grey is the same decal, weaker.
/// <list type="bullet">
/// <item>100 %: the library line stays as the package defines it;</item>
/// <item>0 %: the decal lines are left out;</item>
/// <item>in between: the library's own decal lines are written into the definition in its place, pointing at
/// faded copies of its textures that the mount serves from memory under <see cref="Folder"/>.</item>
/// </list>
/// A library that cannot be read from X-Plane's folder (or uses more than plain decal lines) stays as it is.
/// </summary>
internal sealed class TerrainDecals
{
    public const int Full = 100;
    /// <summary>The package folder the faded decal textures are served under (it exists only in the mount).</summary>
    public const string Folder = "orthostreamxp_decals";
    private const string LibraryPrefix = "lib/g10/decals/";

    private readonly string? _decalsPackage;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _exports;
    private readonly ConcurrentDictionary<string, string?> _replacements = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Served texture name → X-Plane's original file.</summary>
    private readonly ConcurrentDictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<byte[]?>> _faded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _registerLock = new();

    /// <param name="xplane">The X-Plane folder, where the decal libraries are; null when the mount has none.</param>
    /// <param name="strength">0 to 100 percent of the decals' effect.</param>
    public TerrainDecals(string? xplane, int strength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(strength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(strength, Full);
        Strength = strength;
        _decalsPackage = xplane is null ? null : Path.Join(xplane, "Resources", "default scenery", "1000 decals");
        _exports = new Lazy<IReadOnlyDictionary<string, string>>(ReadExports);
    }

    public int Strength { get; }

    /// <summary>
    /// What replaces a definition's "DECAL_LIB <paramref name="library"/>" line: null keeps the line, "" removes
    /// it, otherwise the library's decal lines (each ending in a line break) with faded textures.
    /// </summary>
    public string? Replacement(string library) => Strength switch
    {
        <= 0 => "",
        >= Full => null,
        _ => _replacements.GetOrAdd(library, Inline),
    };

    /// <summary>A faded decal texture (PNG) for a path under <see cref="Folder"/>, or null for any other path.</summary>
    public byte[]? Texture(VirtualPath path)
    {
        if (path.Parts.Length != 2 || !path.IsUnder(Folder) || !_sources.TryGetValue(path.Name, out string? source)) return null;
        return _faded.GetOrAdd(path.Name, _ => new Lazy<byte[]?>(() => Faded(source))).Value;
    }

    private string? Inline(string library)
    {
        if (_decalsPackage is null)
        {
            Log.Warning($"Ground detail {Strength} % needs the X-Plane folder to read {library}; it is kept at full strength");
            return null;
        }
        if (!_exports.Value.TryGetValue(library, out string? file) || !File.Exists(file))
        {
            Log.Warning($"Decal library {library} is not in {_decalsPackage}; it is kept at full strength");
            return null;
        }
        string[] lines = File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n');
        // The header: A (or I), the version, DECAL.
        if (lines.Length < 3 || lines[2].Trim() != "DECAL")
        {
            Log.Warning($"{file} is not a decal library; {library} is kept at full strength");
            return null;
        }
        string folder = Path.GetDirectoryName(file)!;
        var inline = new System.Text.StringBuilder();
        foreach (string raw in lines.Skip(3))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (!tokens[0].StartsWith("DECAL", StringComparison.OrdinalIgnoreCase) || tokens.Length < 2
                || Register(Path.GetFullPath(Path.Join(folder, tokens[^1]))) is not { } served)
            {
                Log.Warning($"{file} has a line this program cannot fade ({line}); {library} is kept at full strength");
                return null;
            }
            inline.Append(string.Join('\t', tokens[..^1])).Append("\t../").Append(Folder).Append('/').Append(served).Append('\n');
        }
        return inline.ToString();
    }

    /// <summary>The name a decal texture is served under, or null when it is missing or its name is taken by another.</summary>
    private string? Register(string source)
    {
        if (!File.Exists(source)) return null;
        string name = Path.GetFileName(source);
        lock (_registerLock)
        {
            string registered = _sources.GetOrAdd(name, source);
            return string.Equals(registered, source, StringComparison.OrdinalIgnoreCase) ? name : null;
        }
    }

    /// <summary>The library paths X-Plane's decal package exports (lib/g10/decals/x.dcl → its file).</summary>
    private Dictionary<string, string> ReadExports()
    {
        var exports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string index = Path.Join(_decalsPackage, "library.txt");
        if (!File.Exists(index)) return exports;
        foreach (string raw in File.ReadLines(index))
        {
            string[] tokens = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 3 && tokens[0] == "EXPORT" && tokens[1].StartsWith(LibraryPrefix, StringComparison.OrdinalIgnoreCase))
                exports.TryAdd(tokens[1], Path.Join(_decalsPackage, tokens[2]));
        }
        return exports;
    }

    private byte[]? Faded(string source)
    {
        try
        {
            byte[] faded = Fade(File.ReadAllBytes(source), Strength);
            Log.Info($"Decal texture {Path.GetFileName(source)} served at {Strength} % strength");
            return faded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warning($"Decal texture {source} could not be faded: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The image with every channel moved towards mid-grey, keeping <paramref name="strength"/> percent of its
    /// distance from it, as a PNG. Alpha is a second (greyscale) decal, not transparency, so it is faded the same
    /// way and never premultiplied.
    /// </summary>
    internal static byte[] Fade(byte[] png, int strength)
    {
        using SKData data = SKData.CreateCopy(png);
        using SKCodec codec = SKCodec.Create(data) ?? throw new InvalidDataException("not a decodable image");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException($"image decode failed: {result}");

        Span<byte> table = stackalloc byte[256];
        double keep = strength / 100.0;
        for (int v = 0; v < 256; v++) table[v] = (byte)Math.Clamp(Math.Round(128 + ((v - 128) * keep)), 0, 255);
        Span<byte> pixels = bitmap.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++) pixels[i] = table[pixels[i]];

        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("PNG encode failed");
        return encoded.ToArray();
    }
}
