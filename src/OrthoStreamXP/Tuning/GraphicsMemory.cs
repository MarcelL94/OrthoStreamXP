using System.Text.Json;
using Microsoft.Win32;

namespace OrthoStreamXP.Tuning;

/// <summary>
/// The graphics memory the photo textures take in X-Plane, per texture size, as flown: the peak over a flight of what
/// X-Plane held. Measured in pixels X-Plane wanted (the same at the same distance, whatever the size), so one flight
/// tells every preset's: a smaller texture just reaches its own size nearer the aircraft. Saved in the log folder as
/// graphics-memory.json; before any flight, the figures of a test flight (Extra, 1 hour, a 3840 px screen).
/// </summary>
internal sealed record GraphicsMemory(DateTime MeasuredUtc, int FlownSize, IReadOnlyDictionary<int, long> PeakBySize)
{
    public const string FileName = "graphics-memory.json";

    /// <summary>X-Plane keeps a texture while its scenery is loaded: what it wanted in the last 20 minutes counts.</summary>
    public static readonly TimeSpan HeldFor = TimeSpan.FromMinutes(20);

    /// <summary>What X-Plane itself takes of a graphics card beside the photo textures, about: the warning's margin.</summary>
    public const long XPlaneOwnBytes = 5L << 30;

    public static readonly int[] Sizes = [1024, 2048, 4096];

    /// <summary>Measured on the test flight of 27 October 2026 (Extra over Poland, 57 min, peaks of 20-minute spans).</summary>
    public static readonly GraphicsMemory TestFlight = new(new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), 4096,
        new Dictionary<int, long> { [1024] = 462_000_000, [2048] = 741_000_000, [4096] = 1_267_000_000 });

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The figures in a history folder (<see cref="Setup.AppPaths.History"/>).</summary>
    public static string PathFor(string history) => Path.Join(history, FileName);

    /// <summary>
    /// The photo textures' graphics memory at a texture size. Above the size flown, X-Plane's wish for detail next to
    /// the aircraft was cut at that size, so the test flight's ratio fills it in.
    /// </summary>
    public long BytesAt(int size)
    {
        if (size <= FlownSize && PeakBySize.TryGetValue(size, out long measured)) return measured;
        long atFlown = PeakBySize.TryGetValue(FlownSize, out long f) ? f : TestFlight.BytesAt(FlownSize);
        return (long)(atFlown * (double)TestFlight.BytesAt(size) / TestFlight.BytesAt(FlownSize));
    }

    /// <summary>A texture's memory in BC1 with its smaller levels: half a byte a pixel, a third more for the levels.</summary>
    public static long TextureBytes(int pixels)
    {
        long side = Math.Max(4, pixels);
        return side * side / 2 * 4 / 3;
    }

    /// <summary>What the textures X-Plane held take at each size.</summary>
    public static Dictionary<int, long> HeldBytes(IEnumerable<int> detail)
    {
        int[] wanted = [.. detail];
        return Sizes.ToDictionary(size => size, size => wanted.Sum(d => TextureBytes(Math.Min(size, d))));
    }

    public static GraphicsMemory Load(string? history)
    {
        try
        {
            if (history is not null && File.Exists(PathFor(history)) && JsonSerializer.Deserialize<GraphicsMemory>(File.ReadAllText(PathFor(history))) is { } saved)
                return saved;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // The test flight's figures, then.
        }
        return TestFlight;
    }

    public void Save(string history)
    {
        try
        {
            AtomicFile.Write(PathFor(history), JsonSerializer.SerializeToUtf8Bytes(this, Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save {PathFor(history)}: {ex.Message}");
        }
    }

    /// <summary>
    /// The graphics card's own memory, from what its driver tells Windows (the largest card's: an integrated one beside
    /// it has little); null when Windows does not say.
    /// </summary>
    public static long? CardBytes()
    {
        try
        {
            using RegistryKey? cards = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cards is null) return null;
            long best = 0;
            foreach (string name in cards.GetSubKeyNames())
            {
                object? value;
                try
                {
                    using RegistryKey? card = cards.OpenSubKey(name);
                    value = card?.GetValue("HardwareInformation.qwMemorySize") ?? card?.GetValue("HardwareInformation.MemorySize");
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    continue; // a key Windows keeps to itself ("Properties"): not a card
                }
                long bytes = value switch
                {
                    long l => l,
                    int i => (uint)i,
                    byte[] { Length: >= 8 } b => BitConverter.ToInt64(b),
                    byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b),
                    _ => 0,
                };
                best = Math.Max(best, bytes);
            }
            return best > 0 ? best : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>Takes the peak of the photo textures' graphics memory over a session, once a minute.</summary>
internal sealed class GraphicsMemoryMeter(int flownSize)
{
    private readonly Dictionary<int, long> _peak = GraphicsMemory.Sizes.ToDictionary(s => s, _ => 0L);

    public void Sample(IEnumerable<int> heldDetail)
    {
        foreach (var (size, bytes) in GraphicsMemory.HeldBytes(heldDetail))
            if (bytes > _peak[size]) _peak[size] = bytes;
    }

    public GraphicsMemory Result() => new(DateTime.UtcNow, flownSize, new Dictionary<int, long>(_peak));
}
