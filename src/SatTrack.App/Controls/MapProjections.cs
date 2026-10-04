using System.Drawing.Drawing2D;
using SatTrack.Core.Geo;

namespace SatTrack.App.Controls;

/// <summary>Turns latitude/longitude into screen points and draws geographic shapes.</summary>
public abstract class MapProjector
{
    protected const double D = Math.PI / 180;

    public int Width { get; }
    public int Height { get; }

    protected MapProjector(int width, int height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>Screen position of a single point (markers, labels). False if off the map.</summary>
    public abstract bool TryPoint(double lat, double lon, out PointF p);

    /// <summary>Adds land/border shapes to paths (used once per cached base layer).</summary>
    public abstract void AddShape(GraphicsPath path, OutlineShape shape, bool closed);

    public abstract void DrawPolyline(Graphics g, Pen pen, IReadOnlyList<GeoPoint> points);

    /// <summary>Fills (and optionally outlines) the area within radiusDeg of center.</summary>
    public abstract void DrawSmallCircle(Graphics g, Brush? fill, Pen? outline, GeoPoint center, double radiusDeg);

    /// <summary>Clip area for the map (the whole control for Mercator, a disk for planar).</summary>
    public abstract void SetClip(Graphics g);

    public abstract void DrawGrid(Graphics g, Pen pen, Palette palette, float fontPx);
}

/// <summary>Mercator centred on the station's longitude, full width shows 360° at zoom 1.</summary>
public sealed class MercatorProjector : MapProjector
{
    private readonly double _lon0;
    private readonly double _s;     // px per degree of longitude
    private readonly double _r;     // px per radian
    private readonly double _yc;    // mercator y at screen center

    public MercatorProjector(int width, int height, double centerLat, double centerLon, double zoom)
        : base(width, height)
    {
        _lon0 = centerLon;
        _s = width * zoom / 360.0;
        _r = _s / D;

        // Keep the view inside about 80°S..84°N.
        double yc = MercY(centerLat);
        double half = height / 2.0;
        double top = MercY(84), bottom = MercY(-80);
        if (top - bottom <= height) yc = (top + bottom) / 2;
        else yc = Math.Clamp(yc, bottom + half, top - half);
        _yc = yc;
    }

    private double MercY(double lat)
    {
        lat = Math.Clamp(lat, -85, 85);
        return _r * Math.Log(Math.Tan(Math.PI / 4 + lat * D / 2));
    }

    private float X(double relLon) => (float)(Width / 2.0 + relLon * _s);
    private float Y(double lat) => (float)(Height / 2.0 - (MercY(lat) - _yc));

    public override bool TryPoint(double lat, double lon, out PointF p)
    {
        p = new PointF(X(GeoMath.Wrap180(lon - _lon0)), Y(lat));
        return p.X >= -50 && p.X <= Width + 50 && p.Y >= -50 && p.Y <= Height + 50;
    }

    public override void AddShape(GraphicsPath path, OutlineShape shape, bool closed)
    {
        for (int k = -1; k <= 1; k++)
        {
            double off = k * 360 - _lon0;
            float x0 = X(shape.MinLon + off), x1 = X(shape.MaxLon + off);
            if (x1 < 0 || x0 > Width) continue;

            var pts = new PointF[shape.Lon.Length];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new PointF(X(shape.Lon[i] + off), Y(shape.Lat[i]));

            path.StartFigure();
            if (closed) path.AddPolygon(pts);
            else path.AddLines(pts);
        }
    }

    public override void DrawPolyline(Graphics g, Pen pen, IReadOnlyList<GeoPoint> points)
    {
        if (points.Count < 2) return;
        var unwrapped = Unwrap(points.Select(p => p.Longitude).ToList());
        for (int k = -2; k <= 2; k++)
        {
            double off = k * 360 - _lon0;
            var pts = new PointF[points.Count];
            float minX = float.MaxValue, maxX = float.MinValue;
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = new PointF(X(unwrapped[i] + off), Y(points[i].Latitude));
                minX = Math.Min(minX, pts[i].X);
                maxX = Math.Max(maxX, pts[i].X);
            }
            if (maxX < 0 || minX > Width) continue;
            g.DrawLines(pen, pts);
        }
    }

    public override void DrawSmallCircle(Graphics g, Brush? fill, Pen? outline, GeoPoint center, double radiusDeg)
    {
        if (radiusDeg <= 0) return;
        var ring = GeoMath.SmallCircle(center, radiusDeg, 180);
        var lons = Unwrap(ring.Select(p => p.Longitude).Append(ring[0].Longitude).ToList());
        double total = lons[^1] - lons[0];
        lons.RemoveAt(lons.Count - 1);

        var geo = new List<(double lon, double lat)>();
        for (int i = 0; i < ring.Length; i++) geo.Add((lons[i], ring[i].Latitude));

        bool containsPole = Math.Abs(total) > 180;
        if (containsPole)
        {
            // The circle goes all the way round the pole: close it along the top or bottom of the map.
            double poleLat = center.Latitude >= 0 ? 89 : -89;
            geo.Add((lons[0] + total, ring[0].Latitude));
            geo.Add((lons[0] + total, poleLat));
            geo.Add((lons[0], poleLat));
        }

        for (int k = -2; k <= 2; k++)
        {
            double off = k * 360 - _lon0;
            var pts = geo.Select(p => new PointF(X(p.lon + off), Y(p.lat))).ToArray();
            float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            if (maxX < 0 || minX > Width) continue;

            if (fill is not null) g.FillPolygon(fill, pts);
            if (outline is not null)
            {
                if (containsPole) g.DrawLines(outline, pts.Take(ring.Length + 1).ToArray());
                else g.DrawPolygon(outline, pts);
            }
        }
    }

    public override void SetClip(Graphics g) => g.SetClip(new Rectangle(0, 0, Width, Height));

    public override void DrawGrid(Graphics g, Pen pen, Palette palette, float fontPx)
    {
        double degPerPx = 1 / _s;
        double spacing = degPerPx * Width > 180 ? 30 : degPerPx * Width > 60 ? 15 : 5;

        double left = _lon0 - Width / 2.0 / _s, right = _lon0 + Width / 2.0 / _s;
        for (double lon = Math.Ceiling(left / spacing) * spacing; lon <= right; lon += spacing)
        {
            float x = X(lon - _lon0);
            g.DrawLine(pen, x, 0, x, Height);
        }
        for (double lat = -75; lat <= 75; lat += spacing)
        {
            float y = Y(lat);
            if (y < 0 || y > Height) continue;
            g.DrawLine(pen, 0, y, Width, y);
        }

        // Equator a touch stronger.
        using var eq = new Pen(Color.FromArgb(Math.Min(255, pen.Color.A + 60), pen.Color), pen.Width);
        float ye = Y(0);
        g.DrawLine(eq, 0, ye, Width, ye);
    }

    private static List<double> Unwrap(List<double> lons)
    {
        var result = new List<double>(lons.Count);
        double prev = double.NaN, acc = 0;
        foreach (double lon in lons)
        {
            if (double.IsNaN(prev)) acc = lon;
            else acc += GeoMath.Wrap180(lon - prev);
            result.Add(acc);
            prev = lon;
        }
        return result;
    }
}

/// <summary>
/// Azimuthal equidistant ("planar") projection centred on the station. Straight lines from
/// the centre are great circles, so the direction to the satellite's ground point reads
/// straight off the map, and distance from the centre is true distance.
/// </summary>
public sealed class PlanarProjector : MapProjector
{
    private readonly GeoPoint _center;
    private readonly float _cx, _cy;
    private readonly double _radiusPx;  // screen radius of maxDist
    private readonly double _maxDist;   // degrees shown to the edge of the disk

    public float DiskRadius => (float)Math.Min(_radiusPx * 180 / _maxDist, _radiusPx);

    public PlanarProjector(int width, int height, GeoPoint center, double zoom) : base(width, height)
    {
        _center = center;
        _cx = width / 2f;
        _cy = height / 2f;
        _radiusPx = Math.Max(10, Math.Min(width, height) / 2.0 - 6);
        _maxDist = 180.0 / zoom;
    }

    private PointF Project(double lat, double lon, out double dist)
    {
        var (d, b) = GeoMath.DistanceBearing(_center, new GeoPoint(lat, lon));
        dist = d;
        double r = d / _maxDist * _radiusPx;
        return new PointF((float)(_cx + r * Math.Sin(b * D)), (float)(_cy - r * Math.Cos(b * D)));
    }

    public override bool TryPoint(double lat, double lon, out PointF p)
    {
        p = Project(lat, lon, out double d);
        return d <= _maxDist && d < 179;
    }

    public override void AddShape(GraphicsPath path, OutlineShape shape, bool closed)
    {
        var pts = new PointF[shape.Lon.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = Project(shape.Lat[i], shape.Lon[i], out double d);
            if (d > 176) return; // shapes touching the antipode can't be drawn faithfully; skip them
        }
        path.StartFigure();
        if (closed) path.AddPolygon(pts);
        else path.AddLines(pts);
    }

    public override void DrawPolyline(Graphics g, Pen pen, IReadOnlyList<GeoPoint> points)
    {
        if (points.Count < 2) return;
        var run = new List<PointF>();
        PointF? prev = null;
        foreach (var gp in points)
        {
            var p = Project(gp.Latitude, gp.Longitude, out double d);
            bool jump = prev is PointF q && Math.Abs(p.X - q.X) + Math.Abs(p.Y - q.Y) > _radiusPx * 0.6;
            if (d > 178 || jump)
            {
                if (run.Count > 1) g.DrawLines(pen, run.ToArray());
                run.Clear();
            }
            if (d <= 178) run.Add(p);
            prev = p;
        }
        if (run.Count > 1) g.DrawLines(pen, run.ToArray());
    }

    public override void DrawSmallCircle(Graphics g, Brush? fill, Pen? outline, GeoPoint center, double radiusDeg)
    {
        if (radiusDeg <= 0) return;
        var ring = GeoMath.SmallCircle(center, radiusDeg, 180);
        var pts = ring.Select(p => Project(p.Latitude, p.Longitude, out _)).ToArray();

        var antipode = new GeoPoint(-_center.Latitude, GeoMath.Wrap180(_center.Longitude + 180));
        bool containsAntipode = GeoMath.DistanceBearing(center, antipode).DistanceDeg < radiusDeg;

        if (fill is not null)
        {
            if (containsAntipode)
            {
                // The circle wraps around the far side of the Earth: fill everything outside the curve.
                float rr = (float)(_radiusPx * 180 / _maxDist);
                using var region = new Region(EllipsePath(_cx, _cy, rr));
                using var inner = new GraphicsPath();
                inner.AddPolygon(pts);
                region.Exclude(inner);
                g.FillRegion(fill, region);
            }
            else
            {
                g.FillPolygon(fill, pts);
            }
        }
        if (outline is not null) g.DrawPolygon(outline, pts);
    }

    public override void SetClip(Graphics g)
    {
        using var path = EllipsePath(_cx, _cy, DiskRadius);
        g.SetClip(path);
    }

    public override void DrawGrid(Graphics g, Pen pen, Palette palette, float fontPx)
    {
        // Range rings with distances in km, and bearing spokes.
        double ringStep = NiceStep(_maxDist / 3);
        var font = Fonts.Get(fontPx);
        using var textBrush = new SolidBrush(palette.TextDim);

        for (double d = ringStep; d < Math.Min(_maxDist, 179.9); d += ringStep)
        {
            float r = (float)(d / _maxDist * _radiusPx);
            g.DrawEllipse(pen, _cx - r, _cy - r, 2 * r, 2 * r);
            double km = d * Math.PI / 180 * GeoMath.EarthRadiusKm;
            string label = km >= 1000 ? $"{km / 1000:0.#}k km" : $"{km:0} km";
            g.DrawString(label, font, textBrush, _cx + r * 0.7071f + 2, _cy - r * 0.7071f - fontPx);
        }

        for (int b = 0; b < 360; b += 30)
        {
            double rad = b * D;
            float ex = (float)(_cx + _radiusPx * Math.Sin(rad)), ey = (float)(_cy - _radiusPx * Math.Cos(rad));
            g.DrawLine(pen, _cx, _cy, ex, ey);
        }

        using var edge = new Pen(palette.PanelEdge, pen.Width * 1.5f);
        float rd = DiskRadius;
        g.DrawEllipse(edge, _cx - rd, _cy - rd, 2 * rd, 2 * rd);

        // Cardinal letters just inside the edge.
        var bold = Fonts.Get(fontPx * 1.15f, true);
        foreach (var (letter, ang) in new[] { ("N", 0), ("E", 90), ("S", 180), ("W", 270) })
        {
            double rad = ang * D;
            float r = rd - fontPx * 1.1f;
            var size = g.MeasureString(letter, bold);
            float x = (float)(_cx + r * Math.Sin(rad)) - size.Width / 2;
            float y = (float)(_cy - r * Math.Cos(rad)) - size.Height / 2;
            g.DrawString(letter, bold, textBrush, x, y);
        }
    }

    public PointF Center => new(_cx, _cy);

    private static GraphicsPath EllipsePath(float cx, float cy, float r)
    {
        var p = new GraphicsPath();
        p.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
        return p;
    }

    private static double NiceStep(double raw)
    {
        foreach (double s in new[] { 2.5, 5, 10, 15, 30, 45, 60 })
            if (s >= raw) return s;
        return 60;
    }
}
