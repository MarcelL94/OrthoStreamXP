using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Paints the lights of a town on a photo texture's night texture, from X-Plane's own roads:
/// <list type="bullet">
/// <item>street lamps along each road, every 28-45 m by its class, a third white LEDs and the rest sodium orange, at
/// random levels and one in eight out; dimmer and farther apart along a country road. Each lamp is the pool of light
/// it casts, centred beside the road, not on it: X-Plane's 3D roads (or an overlay's, like simHeaven's) are drawn
/// over the photo and would hide light painted on the carriageway. Larger roads have lamps on both kerbs in turn,
/// smaller ones on one;</item>
/// <item>a soft glow round the lamps;</item>
/// <item>the houses' light, smooth and faint, in a band along the town streets, and the streets themselves faintly
/// lit (seen where X-Plane draws no 3D road);</item>
/// <item>how built up the place is (<see cref="RoadNetwork.BuiltUp"/>) decides how many lamps are lit (all in a village,
/// a fifth in the open country) and how bright the windows are.</item>
/// </list>
/// Between the lights it is black: X-Plane lights the photo itself at night. Every texture is drawn at the same metres
/// per pixel (<see cref="NightTextures.SideFor"/>), so neighbours of different zoom levels shine alike, and the same
/// texture is always drawn the same way.
/// </summary>
internal sealed class NightPainter(RoadNetworks roads)
{
    /// <summary>The band along a town street its houses' light fills, and that light (the mean of the lit windows it was).</summary>
    private const double WindowBandM = 50, WindowLight = 0.06;
    /// <summary>How soft the town's light is (its Gaussian's sigma), and the share of it that is white rather than warm.</summary>
    private const double TownSigmaM = 8;
    private const float TownWhite = 0.25f;
    private const float StreetWeight = 0.12f, LampWeight = 1.0f, GlowWeight = 0.7f;
    private const double LedShare = 0.35, LampsOut = 0.12, LampMin = 0.55, LampMax = 1.0;
    /// <summary>How far past the kerb a lamp's pool of light is centred.</summary>
    private const double LampSetbackM = 2;
    /// <summary>How wide the glow round the lamps is (its Gaussian's sigma).</summary>
    private const double GlowSigmaM = 15;
    /// <summary>The share of lamps lit where nothing is built up; all are lit from <see cref="VillageBuiltUp"/> on.</summary>
    private const double LitShareMin = 0.2, VillageBuiltUp = 0.25;
    /// <summary>A lamp's light where nothing is built up, against 2 - this in a city centre.</summary>
    private const double LampCountry = 0.8;
    /// <summary>The windows' light where nothing is built up, and how much more in a city centre.</summary>
    private const double WindowsCountry = 0.2, WindowsCity = 1.3;
    /// <summary>What the lights drawn are multiplied by.</summary>
    public const float Gain = 0.9f;
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
        return anyRoad && Compose(canvas, side, metresPerPixel);
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

        // The lamps: one every spacing along the road from a random start, beside it (on one kerb, or on both in
        // turn). Every draw is made whatever its outcome, so one lamp's fate never moves the next lamps.
        double spacing = look.LampSpacingM * (rural ? RoadClasses.RuralSpacing : 1) / metresPerPixel;
        double setback = (look.WidthM / 2 + LampSetbackM) / metresPerPixel;
        float[] lamps = random.NextDouble() < LedShare ? canvas.Led : canvas.Sodium;
        int kerb = random.NextDouble() < 0.5 ? 1 : -1;
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
                // Off the road to the kerb's side: (-dy, dx) is square to the road.
                double lx = x[i] + t * dx - kerb * setback * dy / length, ly = y[i] + t * dy + kerb * setback * dx / length;
                if (look.LampsBothSides) kerb = -kerb;
                // A lamp outside the texture is its neighbour's to draw, which places its own lamps.
                if (!working || !lit || lx < 0 || ly < 0 || lx >= canvas.Side || ly >= canvas.Side) continue;
                NightRaster.Spot(lamps, canvas.Side, lx, ly, lampSigma, (float)level);
            }
            travelled += length;
        }
    }

    /// <summary>The lamps, their glow and the town's light together, in their colours, into the RGB picture; false when all black.</summary>
    private static bool Compose(NightWorkspace canvas, int side, double metresPerPixel)
    {
        double sigma = Math.Max(1.5, GlowSigmaM / metresPerPixel);
        // The glow is the lamps' alone: they stand beside the road, so it lights the verges, not the carriageway
        // a 3D road covers.
        float[] warmGlow = canvas.WarmGlow, coolGlow = canvas.CoolGlow;
        NightRaster.Blur(canvas.Sodium, side, sigma, canvas.Across, warmGlow);
        NightRaster.Blur(canvas.Led, side, sigma, canvas.Across, coolGlow);

        // The houses' light beside the streets and the streets' own faint light, as one smooth light: drawn pixel by
        // pixel (a window here, none there) it was a speckle, which X-Plane's enlarging beside the aircraft made blotches,
        // and a street's hard edge came out in steps.
        float[] town = canvas.Windows;
        for (int i = 0; i < town.Length; i++)
            if (canvas.Street[i] > 0) town[i] = canvas.Street[i] * StreetWeight;
        NightRaster.Blur(town, side, Math.Max(1, TownSigmaM / metresPerPixel), canvas.Across, town);

        byte[] rgb = canvas.Rgb;
        bool any = false;
        for (int i = 0; i < side * side; i++)
        {
            if (warmGlow[i] == 0 && coolGlow[i] == 0 && town[i] == 0) continue; // the glows cover the lamps and streets
            float warm = warmGlow[i] * GlowWeight + canvas.Sodium[i] * LampWeight + town[i] * (1 - TownWhite);
            float cool = coolGlow[i] * GlowWeight + canvas.Led[i] * LampWeight + town[i] * TownWhite;
            byte r = ToByte((warm * Sodium[0] + cool * Led[0]) * Gain), g = ToByte((warm * Sodium[1] + cool * Led[1]) * Gain), b = ToByte((warm * Sodium[2] + cool * Led[2]) * Gain);
            if (Math.Max(r, Math.Max(g, b)) < Floor) continue;
            (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (r, g, b);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// A lamp's pool of light holds as much light as a square this wide at full strength (about 2.5 pixels of a ZL16
    /// texture painted at 1024 px in Europe; a street lamp lights the ground some 15-20 m across), at every resolution:
    /// finer painting makes it rounder, not dimmer.
    /// </summary>
    internal const double LampSizeM = 17;

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
