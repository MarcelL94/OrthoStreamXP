using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

/// <summary>Brushes and text for the report drawings, from the window's theme.</summary>
internal abstract class ReportDrawing : FrameworkElement
{
    public static readonly DependencyProperty ReportProperty = DependencyProperty.Register(nameof(Report), typeof(FlightReport), typeof(ReportDrawing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public FlightReport? Report
    {
        get => (FlightReport?)GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    protected Brush Theme(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    protected Brush Accent => Theme("AccentFillColorDefaultBrush", Colors.SteelBlue);
    protected Brush Text => Theme("TextFillColorSecondaryBrush", Colors.Gray);
    protected Brush Grid => Theme("CardStrokeColorDefaultBrush", Color.FromArgb(40, 128, 128, 128));
    protected Brush Builds => Theme("SystemFillColorSuccessBrush", Colors.SeaGreen);
    protected Brush StandIns => Theme("SystemFillColorCriticalBrush", Colors.IndianRed);

    protected FormattedText Label(string text) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Text, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected static Brush Faded(Brush brush, double opacity)
    {
        Brush faded = brush.CloneCurrentValue();
        faded.Opacity = opacity;
        faded.Freeze();
        return faded;
    }
}

/// <summary>
/// The session over time: download speed (filled, left scale), textures built a minute (line, right scale), and a red
/// mark under each moment X-Plane's reads got stand-ins.
/// </summary>
internal sealed class FlightChart : ReportDrawing
{
    protected override void OnRender(DrawingContext dc)
    {
        if (Report is not { Samples.Count: > 1 } report) return;
        const double left = 44, right = 44, top = 8, bottom = 22, marks = 8;
        double width = ActualWidth - left - right, height = ActualHeight - top - bottom - marks;
        if (width <= 10 || height <= 10) return;
        double end = Math.Max(1, report.Samples[^1].Seconds);
        double X(double seconds) => left + seconds / end * width;

        var speed = report.Speed().ToList();
        double topSpeed = Math.Max(1, speed.Max(s => s.Mbits));
        var builds = BuildsPerMinute(report).ToList();
        double topBuilds = Math.Max(1, builds.Count == 0 ? 1 : builds.Max(b => b.PerMinute));

        // Grid and scales.
        var gridPen = new Pen(Grid, 1);
        for (int i = 0; i <= 4; i++)
        {
            double y = top + height * i / 4;
            dc.DrawLine(gridPen, new Point(left, y), new Point(left + width, y));
            dc.DrawText(Label((topSpeed * (4 - i) / 4).ToString("0", CultureInfo.CurrentCulture)), new Point(4, y - 7));
            dc.DrawText(Label((topBuilds * (4 - i) / 4).ToString("0", CultureInfo.CurrentCulture)), new Point(left + width + 6, y - 7));
        }
        dc.DrawText(Label("Mbit/s"), new Point(4, top + height + 4));
        FormattedText perMinute = Label("built/min");
        dc.DrawText(perMinute, new Point(ActualWidth - perMinute.Width - 2, top + height + 4));
        double step = TimeStep(end);
        for (double s = 0; s <= end; s += step)
        {
            FormattedText label = Label(s == 0 ? "0" : $"{s / 60:0} min");
            dc.DrawText(label, new Point(Math.Min(X(s) - label.Width / 2, ActualWidth - label.Width), top + height + marks + 6));
        }

        // Download speed, filled.
        var area = new StreamGeometry();
        using (StreamGeometryContext g = area.Open())
        {
            g.BeginFigure(new Point(X(report.Samples[0].Seconds), top + height), true, true);
            foreach (var (seconds, mbits) in speed) g.LineTo(new Point(X(seconds), top + height - mbits / topSpeed * height), true, true);
            g.LineTo(new Point(X(speed[^1].Seconds), top + height), true, true);
        }
        area.Freeze();
        dc.DrawGeometry(Faded(Accent, 0.35), new Pen(Accent, 1.2), area);

        // Textures built a minute.
        if (builds.Count > 1)
        {
            var line = new StreamGeometry();
            using (StreamGeometryContext g = line.Open())
            {
                g.BeginFigure(new Point(X(builds[0].Seconds), top + height - builds[0].PerMinute / topBuilds * height), false, false);
                foreach (var (seconds, perMin) in builds.Skip(1)) g.LineTo(new Point(X(seconds), top + height - perMin / topBuilds * height), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, new Pen(Builds, 1.6), line);
        }

        // Stand-ins: a mark under each moment they happened.
        var markPen = new Pen(StandIns, 2);
        for (int i = 1; i < report.Samples.Count; i++)
            if (report.Samples[i].StandIns > report.Samples[i - 1].StandIns)
                dc.DrawLine(markPen, new Point(X(report.Samples[i].Seconds), top + height + 1), new Point(X(report.Samples[i].Seconds), top + height + marks));
    }

    /// <summary>Textures built a minute, over the minute before each sample.</summary>
    private static IEnumerable<(double Seconds, double PerMinute)> BuildsPerMinute(FlightReport report)
    {
        IReadOnlyList<ReportSample> s = report.Samples;
        for (int i = 0, j = 0; i < s.Count; i++)
        {
            while (j < i && s[i].Seconds - s[j].Seconds > 60) j++;
            if (i > j && s[i].Seconds > s[j].Seconds) yield return (s[i].Seconds, (s[i].Builds - s[j].Builds) * 60 / (s[i].Seconds - s[j].Seconds));
        }
    }

    private static double TimeStep(double seconds) =>
        new[] { 60.0, 300, 600, 900, 1800, 3600, 7200 }.FirstOrDefault(step => seconds / step <= 8, 14400);
}

/// <summary>
/// The piece of the globe the route map shows: the route with a margin, at least about 20 km across (a parked aircraft
/// shows its surroundings), in the map's proportions; longitudes narrowed by the cosine of the latitude, as on a map.
/// </summary>
internal static class RouteView
{
    /// <summary>The route map's size, fixed so its picture is made once at the right proportions.</summary>
    public const double Width = 300, Height = 170;

    public static GeoBounds? For(FlightReport report)
    {
        var points = report.Positions.ToList();
        if (points.Count == 0) return null;
        double south = points.Min(p => p.Lat!.Value), north = points.Max(p => p.Lat!.Value);
        double west = points.Min(p => p.Lon!.Value), east = points.Max(p => p.Lon!.Value);
        double midLat = (south + north) / 2, midLon = (west + east) / 2, squeeze = Math.Cos(midLat * Math.PI / 180);
        double aspect = Width / Height;
        double spanLat = Math.Max(Math.Max(north - south, (east - west) * squeeze / aspect), 0.2) * 1.25;
        double spanLon = spanLat * aspect / squeeze;
        return new GeoBounds(midLat - spanLat / 2, midLon - spanLon / 2, midLat + spanLat / 2, midLon + spanLon / 2);
    }
}

/// <summary>
/// The route flown, north up, over a picture of the place from the cache's low-zoom base, with the cities around it:
/// a green dot at the start, a dark one at the end, and a red dot where X-Plane's reads got stand-ins (bigger for more).
/// </summary>
internal sealed class RouteMap : ReportDrawing
{
    public static readonly DependencyProperty BackdropProperty = DependencyProperty.Register(nameof(Backdrop), typeof(ImageSource), typeof(RouteMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The place under the route, for <see cref="RouteView.For"/>'s piece of the globe.</summary>
    public ImageSource? Backdrop
    {
        get => (ImageSource?)GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Report is not { } report || RouteView.For(report) is not { } view) return;
        var points = report.Positions.ToList();
        double w = ActualWidth, h = ActualHeight;
        Point At(double lat, double lon) => new((lon - view.West) / (view.East - view.West) * w, (view.North - lat) / (view.North - view.South) * h);
        Point Of(ReportSample s) => At(s.Lat!.Value, s.Lon!.Value);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 6, 6));
        dc.DrawRectangle(Faded(Accent, 0.08), null, new Rect(0, 0, w, h));
        if (Backdrop is not null) dc.DrawImage(Backdrop, new Rect(0, 0, w, h));

        // Cities: a dot and the name on a dark tag, readable on any picture.
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Name(string text) => new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, dip);
        var tag = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0));
        tag.Freeze();
        foreach (CityLabel city in Cities.Place(view, (lat, lon) => At(lat, lon), name => { FormattedText f = Name(name); return new Size(f.Width + 8, f.Height); }, w, h, max: 8))
        {
            FormattedText text = Name(city.Name);
            dc.DrawEllipse(Brushes.White, new Pen(tag, 1), new Point(city.X, city.Y), 2.5, 2.5);
            dc.DrawRoundedRectangle(tag, null, new Rect(city.X + 5, city.Y - text.Height / 2 - 1, text.Width + 8, text.Height + 2), 3, 3);
            dc.DrawText(text, new Point(city.X + 9, city.Y - text.Height / 2));
        }

        // The route: white under the accent colour, so it shows on any picture.
        var route = new StreamGeometry();
        using (StreamGeometryContext g = route.Open())
        {
            ReportSample? last = null;
            foreach (ReportSample s in points)
            {
                // A jump (a move to another place) starts a new line.
                if (last is null || Scenery.Geo.DistanceM(last.Lat!.Value, last.Lon!.Value, s.Lat!.Value, s.Lon!.Value) > 50_000)
                    g.BeginFigure(Of(s), false, false);
                else g.LineTo(Of(s), true, true);
                last = s;
            }
        }
        route.Freeze();
        dc.DrawGeometry(null, new Pen(Brushes.White, 5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, route);
        dc.DrawGeometry(null, new Pen(Accent, 3) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, route);

        IReadOnlyList<ReportSample> all = report.Samples;
        ReportSample? where = null;
        var outline = new Pen(Brushes.White, 1.5);
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Lat is not null) where = all[i];
            long more = i == 0 ? 0 : all[i].StandIns - all[i - 1].StandIns;
            if (more > 0 && where is not null)
            {
                double r = 3 + Math.Min(5, Math.Log2(more));
                dc.DrawEllipse(Faded(StandIns, 0.85), outline, Of(where), r, r);
            }
        }
        dc.DrawEllipse(Builds, outline, Of(points[0]), 5, 5);
        dc.DrawEllipse(Theme("TextFillColorPrimaryBrush", Colors.Black), outline, Of(points[^1]), 5, 5);
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(Grid, 1), new Rect(0.5, 0.5, w - 1, h - 1), 6, 6);
    }
}
