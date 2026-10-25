using System.Collections.Concurrent;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// The photo textures used by the scenery X-Plane has loaded. When X-Plane loads a DSF it opens every terrain
/// definition (.ter) the DSF uses, 20-45 s before it asks for their textures, and each .ter name gives its
/// texture (..._sea_overlay.ter → ....dds). So this lists exactly the textures that exist around the aircraft,
/// at whatever zoom the package uses there, whether the package ships .ter files or they are generated.
/// </summary>
internal sealed class TextureCatalog
{
    internal readonly record struct Entry(VirtualPath Texture, double Lat, double Lon);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A texture seen for the first time: X-Plane is loading the scenery that uses it.</summary>
    public event Action<Entry>? EntryAdded;

    /// <summary>X-Plane asked for a known texture: where it is loading right now.</summary>
    public event Action<Entry>? TextureRequested;

    public int Count => _entries.Count;

    public ICollection<Entry> Entries => _entries.Values;

    /// <summary>Records the texture behind a terrain definition X-Plane opened.</summary>
    public void AddTerrain(VirtualPath terrain)
    {
        if (!terrain.IsTerrain || TerrainDefinitions.TextureOf(terrain.Name, out _, out _) is not { } name) return;
        if (_entries.ContainsKey(name.Stem)) return;
        var (lat, lon) = Tiles.TileToLatLon(name.Col + Tiles.TextureSpan / 2.0, name.Row + Tiles.TextureSpan / 2.0, name.Zoom);
        var entry = new Entry(VirtualPath.Of("textures", name.Stem + ".dds"), lat, lon);
        if (_entries.TryAdd(name.Stem, entry)) EntryAdded?.Invoke(entry);
    }

    /// <summary>Notes that X-Plane asked for a texture.</summary>
    public void NoteTextureRequested(VirtualPath texture)
    {
        if (TextureRequested is { } handler && _entries.TryGetValue(Path.GetFileNameWithoutExtension(texture.Name), out Entry entry))
            handler(entry);
    }
}
