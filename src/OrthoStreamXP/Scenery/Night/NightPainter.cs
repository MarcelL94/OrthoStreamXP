using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Paints the lights of a town on a photo texture's night texture, from X-Plane's own roads:
/// <list type="bullet">
/// <item>street lamps along each road, every 28-45 m by its class, a third white LEDs and the rest sodium orange, at
/// random levels and one in eight out; dimmer and farther apart along a country road;</item>
/// <item>a soft glow round the lamps and the larger streets;</item>
/// <item>windows in a band along the town streets, a few bright, most dim;</item>
/// <item>how built up the place is (<see cref="RoadNetwork.BuiltUp"/>) decides how many lamps are lit (all in a village,
/// a fifth in the open country) and how bright the windows are.</item>
/// </list>
/// Between the lights it is black: X-Plane lights the photo itself at night. Every texture is drawn at the same metres
/// per pixel (<see cref="NightTextures.SideFor"/>), so neighbours of different zoom levels shine alike, and the same
/// texture is always drawn the same way.
/// </summary>
internal sealed class NightPainter(RoadNetworks roads)
{
    private const double WindowBandM = 50, WindowLight = 0.10;
    private const float StreetWeight = 0.12f, LampWeight = 1.0f, GlowWeight = 0.35f;
    private const double LedShare = 0.35, LampsOut = 0.12, LampMin = 0.35, LampMax = 0.80;
    /// <summary>The share of lamps lit where nothing is built up; all are lit from <see cref="VillageBuiltUp"/> on.</summary>
    private const double LitShareMin = 0.2, VillageBuiltUp = 0.25;
    /// <summary>A lamp's light where nothing is built up, against 2 - this in a city centre.</summary>
    private const double LampCountry = 0.8;
    /// <summary>The windows' light where nothing is built up, and how much more in a city centre.</summary>
    private const double WindowsCountry = 0.2, WindowsCity = 1.3;
    /// <summary>What the lights drawn are multiplied by.</summary>
    public const float Gain = 0.75f;
    /// <summary>Light fainter than this (out of 255, in its brightest channel) is black: BC1 cannot keep its colour.</summary>
    public const int Floor = 6;
    /// <summary>How far round the texture roads are looked for: their glow reaches in.</summary>
    private const double MarginDegrees = 0.005;

    private static readonly float[] Sodium = [1.00f, 0.70f, 0.36f], Led = [0.95f, 0.92f, 0.85f];

    /// <summary>
    /// Paints the texture's lights into <paramref name="canvas"/>'s RGB picture, as large as the workspace (black where
    /// there is no light); false when it has none at all.
    /// </summary>
    public bool Paint(TextureName name, NightWorkspace canvas)
    {
        int side = canvas.Side;
        var (north, west) = Tiles.TileToLatLon(name.Col, name.Row, name.Zoom);
        var (south, east) = Tiles.TileToLatLon(name.Col + Tiles.TextureSpan, name.Row + Tiles.TextureSpan, name.Zoom);
        double metresPerPixel = name.GroundSizeM / side;
        var random = new Random(Seed(name));
        var projection = new Projection(name, side);
        var chains = new List<int>();
        bool anyRoad = false;
        for (int lat = (int)Math.Floor(south - MarginDegrees); lat <= (int)Math.Floor(north + MarginDegrees); lat++)
            for (int lon = (int)Math.Floor(west - MarginDegrees); lon <= (int)Math.Floor(east + MarginDegrees); lon++)
            {
                if (roads.For(lat, lon) is not { } network) continue;
                chains.Clear();
                network.Near(south - MarginDegrees, west - MarginDegrees, north + MarginDegrees, east + MarginDegrees, chains);
                foreach (int chain in chains) DrawRoad(network, chain, projection, metresPerPixel, canvas, random);
                anyRoad |= chains.Count > 0;
            }
        return anyRoad && Compose(canvas, side, metresPerPixel, random);
    }

    /// <summary>The same lamps for a texture every time it is painted.</summary>
    private static int Seed(TextureName name) => unchecked((name.Row * 73_856_093) ^ (name.Col * 19_349_663) ^ (name.Zoom * 83_492_791));

    private static void DrawRoad(RoadNetwork network, int chain, Projection projection, double metresPerPixel, NightWorkspace canvas, Random random)
    {
        RoadClass roadClass = network.ClassOf(chain);
        bool rural = network.IsRural(chain);
        RoadLook look = RoadClasses.Look(roadClass);
        var (lonMemory, latMemory) = network.Points(chain);
        ReadOnlySpan<float> lon = lonMemory.Span, lat = latMemory.Span;
        int count = lon.Length;
        Span<double> x = count <= 256 ? stackalloc double[count] : new double[count];
        Span<double> y = count <= 256 ? stackalloc double[count] : new double[count];
        for (int i = 0; i < count; i++) (x[i], y[i]) = projection.Pixel(network.South + lat[i], network.West + lon[i]);

        double brightness = look.Brightness * (rural ? RoadClasses.RuralBrightness : 1);
        int middle = count / 2;
        double builtUp = network.BuiltUp(network.South + lat[middle], network.West + lon[middle]);
        double litShare = LitShareMin + (1 - LitShareMin) * Math.Min(1, builtUp / VillageBuiltUp);

        // The street itself, as much as its lamps are lit, and the windows along a town street.
        double streetWidth = Math.Max(1, Math.Round(look.WidthM / metresPerPixel));
        float street = (float)(brightness * litShare);
        bool houses = look.LinedWithHouses && !rural;
        double bandWidth = Math.Max(1, Math.Round(WindowBandM / metresPerPixel));
        float windows = (float)(WindowLight * (WindowsCountry + WindowsCity * builtUp));
        for (int i = 0; i < count - 1; i++)
        {
            NightRaster.Line(canvas.Street, canvas.Side, x[i], y[i], x[i + 1], y[i + 1], streetWidth, street);
            if (houses) NightRaster.Line(canvas.Windows, canvas.Side, x[i], y[i], x[i + 1], y[i + 1], bandWidth, windows);
        }

        // The lamps: one every spacing along the road from a random start. Every draw is made whatever its outcome,
        // so one lamp's fate never moves the next lamps.
        double spacing = look.LampSpacingM * (rural ? RoadClasses.RuralSpacing : 1) / metresPerPixel;
        float[] lamps = random.NextDouble() < LedShare ? canvas.Led : canvas.Sodium;
        double next = random.NextDouble() * spacing, travelled = 0;
        double lampScale = LampCountry + 2 * (1 - LampCountry) * builtUp, lampSigma = LampSigma(metresPerPixel);
        for (int i = 0; i < count - 1; i++)
        {
            double dx = x[i + 1] - x[i], dy = y[i + 1] - y[i], length = Math.Sqrt(dx * dx + dy * dy);
            for (; next <= travelled + length && length > 0; next += spacing)
            {
                double t = (next - travelled) / length;
                bool working = random.NextDouble() >= LampsOut, lit = random.NextDouble() < litShare;
                double level = brightness * (LampMin + (LampMax - LampMin) * random.NextDouble()) * lampScale;
                double lx = x[i] + t * dx, ly = y[i] + t * dy;
                // A lamp outside the texture is its neighbour's to draw, which places its own lamps.
                if (!working || !lit || lx < 0 || ly < 0 || lx >= canvas.Side || ly >= canvas.Side) continue;
                NightRaster.Spot(lamps, canvas.Side, lx, ly, lampSigma, (float)level);
            }
            travelled += length;
        }
    }

    /// <summary>The lamps, their glow, the streets and the windows together, in their colours, into the RGB picture; false when all black.</summary>
    private static bool Compose(NightWorkspace canvas, int side, double metresPerPixel, Random random)
    {
        double sigma = Math.Max(1.5, 12 / metresPerPixel);
        // The warm glow's source is made in the cool glow's picture, which is filled only after.
        float[] warmGlow = canvas.WarmGlow, coolGlow = canvas.CoolGlow;
        for (int i = 0; i < coolGlow.Length; i++) coolGlow[i] = canvas.Street[i] * 0.5f + canvas.Sodium[i];
        NightRaster.Blur(coolGlow, side, sigma, canvas.Across, warmGlow);
        NightRaster.Blur(canvas.Led, side, sigma, canvas.Across, coolGlow);

        byte[] rgb = canvas.Rgb;
        bool any = false;
        for (int i = 0; i < side * side; i++)
        {
            if (warmGlow[i] == 0 && coolGlow[i] == 0 && canvas.Windows[i] == 0) continue; // the glow covers the streets and lamps
            float warm = warmGlow[i] * GlowWeight + canvas.Street[i] * StreetWeight + canvas.Sodium[i] * LampWeight;
            float cool = coolGlow[i] * GlowWeight + canvas.Led[i] * LampWeight;
            if (canvas.Windows[i] > 0)
            {
                // A few windows bright, most dim (the sixth power of an even draw); three in ten lit white, the rest warm.
                double draw = random.NextDouble(), cubed = draw * draw * draw;
                double spark = cubed * cubed * 2.5 + 0.25;
                float window = (float)(canvas.Windows[i] * spark);
                if (random.NextDouble() < 0.3) cool += window;
                else warm += window;
            }
            byte r = ToByte((warm * Sodium[0] + cool * Led[0]) * Gain), g = ToByte((warm * Sodium[1] + cool * Led[1]) * Gain), b = ToByte((warm * Sodium[2] + cool * Led[2]) * Gain);
            if (Math.Max(r, Math.Max(g, b)) < Floor) continue;
            (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (r, g, b);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// A lamp's pool of light holds as much light as a square this wide at full strength (two pixels of a ZL16 texture
    /// painted at 1024 px in Europe), at every resolution: finer painting makes it rounder, not dimmer.
    /// </summary>
    private const double LampSizeM = 13;

    /// <summary>
    /// The width, in pixels, of a lamp's round soft spot: a Gaussian holding as much light as the square of
    /// <see cref="LampSizeM"/> (2π sigma² = its area).
    /// </summary>
    internal static double LampSigma(double metresPerPixel) => LampSizeM / metresPerPixel / Math.Sqrt(2 * Math.PI);

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value * 255 + 0.5f), 0, 255);

    /// <summary>Where a place falls on the texture, in pixels from its north-west corner (Web Mercator, as the photo).</summary>
    private readonly struct Projection
    {
        private readonly double _tiles, _scale, _col, _row;

        public Projection(TextureName name, int side)
        {
            _tiles = 1L << name.Zoom;
            _scale = (double)side / Tiles.TextureSpan;
            (_col, _row) = (name.Col, name.Row);
        }

        public (double X, double Y) Pixel(double lat, double lon)
        {
            double tileX = (lon + 180) / 360 * _tiles;
            double radians = lat * Math.PI / 180;
            double tileY = (1 - Math.Log(Math.Tan(radians) + 1 / Math.Cos(radians)) / Math.PI) / 2 * _tiles;
            return ((tileX - _col) * _scale, (tileY - _row) * _scale);
        }
    }
}
