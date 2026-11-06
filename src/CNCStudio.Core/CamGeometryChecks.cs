using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// Checks that relate a toolpath to the material and to the other parts in the design.
/// All coordinates are stock-local (the design view's coordinate system).
/// </summary>
public static class CamGeometryChecks
{
    /// <summary>Above this many segment–edge tests the part-to-part check is skipped (and says so).</summary>
    public const long PartCheckBudget = 4_000_000;

    /// <summary>Shape and toolpath against the material's edges.</summary>
    public static IReadOnlyList<CamFinding> MaterialBounds(CncDocument doc, DesignShape shape,
        CamOperation op, ContourPreview preview)
    {
        var list = new List<CamFinding>();
        double w = doc.MaterialWidth, h = doc.MaterialHeight;
        double left = shape.X, top = shape.Y, right = shape.X + shape.Width, bottom = shape.Y + shape.Height;
        bool fullyOutside = right <= 0 || bottom <= 0 || left >= w || top >= h;
        bool partlyOutside = left < -1e-9 || top < -1e-9 || right > w + 1e-9 || bottom > h + 1e-9;
        if (fullyOutside)
            list.Add(new CamFinding(CamSeverity.Warning, "ShapeOffStock",
                "Objektet ligger helt utanför materialet – fräsen skär bara i luft eller i offerskivan.", shape.Id));
        else if (partlyOutside)
            list.Add(new CamFinding(CamSeverity.Warning, "ShapeOutsideStock",
                "Objektet ligger delvis utanför materialet – den färdiga delen blir ofullständig.", shape.Id));
        else
        {
            // Shape inside, but the tool's edge may still pass beyond the material edge.
            double r = op.ToolDiameter / 2;
            var all = preview.Centerlines.Count > 0 ? preview.Centerlines.SelectMany(p => p) : preview.Centerline;
            if (all.Any(p => p.X - r < -1e-9 || p.Y - r < -1e-9 || p.X + r > w + 1e-9 || p.Y + r > h + 1e-9))
                list.Add(new CamFinding(CamSeverity.Info, "PathBeyondStock",
                    "Fräsen går delvis utanför materialkanten (skär där i luft). Kontrollera att inget annat finns där.",
                    shape.Id));
        }
        return list;
    }

    /// <summary>
    /// Does the tool's swept band (centerline ± radius) cut into another part that has its own
    /// CAM operation? Each affected segment is reported once per other part.
    /// </summary>
    public static IReadOnlyList<CamFinding> PartClearance(DesignShape shape, CamOperation op,
        ContourPreview preview, IEnumerable<(DesignShape Shape, string Label)> otherParts)
    {
        var list = new List<CamFinding>();
        double r = op.ToolDiameter / 2;
        var paths = preview.Centerlines.Count > 0 ? preview.Centerlines :
            new IReadOnlyList<CamPoint>[] { preview.Centerline };
        var (pMinX, pMinY, pMaxX, pMaxY) = Bounds(paths.SelectMany(p => p));
        long budget = PartCheckBudget;
        foreach (var (other, label) in otherParts)
        {
            if (other.Id == shape.Id) continue;
            // A pocket or inside contour placed within another part is a feature OF that part
            // (e.g. a recess in a board that is cut out afterwards) – cutting into it is intended.
            if (op.Kind is CamOperationKind.Pocket or CamOperationKind.Inside && LiesWithin(shape, other)) continue;
            // Quick reject: the tool band cannot reach the other part's bounding box.
            if (other.X > pMaxX + r || other.Y > pMaxY + r ||
                other.X + other.Width < pMinX - r || other.Y + other.Height < pMinY - r) continue;
            var polygons = Outline(other);
            long edges = polygons.Sum(p => (long)p.Count);
            long segments = paths.Sum(p => (long)Math.Max(0, p.Count - 1));
            budget -= edges * segments;
            if (budget < 0)
            {
                list.Add(new CamFinding(CamSeverity.Info, "PartCheckSkipped",
                    "För komplex geometri: kontrollen om banan skär in i andra delar hoppades över.", shape.Id));
                return list;
            }
            for (int pi = 0; pi < paths.Count; pi++)
            {
                var path = paths[pi];
                for (int si = 1; si < path.Count; si++)
                {
                    var a = path[si - 1]; var b = path[si];
                    double sx0 = Math.Min(a.X, b.X) - r, sx1 = Math.Max(a.X, b.X) + r;
                    double sy0 = Math.Min(a.Y, b.Y) - r, sy1 = Math.Max(a.Y, b.Y) + r;
                    if (other.X > sx1 || other.Y > sy1 || other.X + other.Width < sx0 || other.Y + other.Height < sy0)
                        continue;
                    if (BandHitsRegion(a, b, r, polygons))
                        list.Add(new CamFinding(CamSeverity.Warning, "CutsOtherPart",
                            $"Banan skär in i {label}.", shape.Id, null, pi, si - 1));
                }
            }
        }
        return list;
    }

    /// <summary>True if <paramref name="inner"/> lies inside <paramref name="outer"/>'s filled region (not in a hole).</summary>
    public static bool LiesWithin(DesignShape inner, DesignShape outer)
    {
        if (inner.X < outer.X - 1e-6 || inner.Y < outer.Y - 1e-6 ||
            inner.X + inner.Width > outer.X + outer.Width + 1e-6 || inner.Y + inner.Height > outer.Y + outer.Height + 1e-6)
            return false;
        var polygons = Outline(outer);
        var innerOutline = Outline(inner);
        // Every vertex of the inner shape's outline must be inside the outer region.
        return innerOutline.SelectMany(p => p).All(v => Inside(v, polygons));
    }

    /// <summary>True if the segment's tool band (radius r) overlaps the filled even-odd region.</summary>
    public static bool BandHitsRegion(CamPoint a, CamPoint b, double r, IReadOnlyList<IReadOnlyList<CamPoint>> polygons)
    {
        const double eps = 1e-6;
        foreach (var poly in polygons)
            for (int i = 0; i < poly.Count; i++)
                if (SegmentDistance(a, b, poly[i], poly[(i + 1) % poly.Count]) < r - eps) return true;
        // No boundary within reach: the band is entirely inside or entirely outside.
        return Inside(new CamPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2), polygons);
    }

    /// <summary>Closed outline polygons of a shape (no repeated end point).</summary>
    public static IReadOnlyList<IReadOnlyList<CamPoint>> Outline(DesignShape s)
    {
        switch (s.Kind)
        {
            case ShapeKind.Rectangle:
                return new[] { (IReadOnlyList<CamPoint>)new[] {
                    new CamPoint(s.X, s.Y), new CamPoint(s.X + s.Width, s.Y),
                    new CamPoint(s.X + s.Width, s.Y + s.Height), new CamPoint(s.X, s.Y + s.Height) } };
            case ShapeKind.Ellipse:
            {
                // Circumscribed polygon: never smaller than the ellipse (conservative).
                const int n = 72;
                double grow = 1 / Math.Cos(Math.PI / n);
                double a = s.Width / 2, b = s.Height / 2, cx = s.X + a, cy = s.Y + b;
                var pts = new CamPoint[n];
                for (int i = 0; i < n; i++)
                {
                    double t = 2 * Math.PI * i / n;
                    pts[i] = new CamPoint(cx + a * grow * Math.Cos(t), cy + b * grow * Math.Sin(t));
                }
                return new[] { (IReadOnlyList<CamPoint>)pts };
            }
            default:
                return (s.Contours ?? new List<VectorContour>())
                    .Where(c => c.Closed && c.Points.Count >= 3)
                    .Select(c => (IReadOnlyList<CamPoint>)c.Points
                        .Select(p => new CamPoint(s.X + p.X * s.Width, s.Y + p.Y * s.Height)).ToList())
                    .ToList();
        }
    }

    /// <summary>Even-odd point-in-region test over all polygons.</summary>
    public static bool Inside(CamPoint p, IReadOnlyList<IReadOnlyList<CamPoint>> polygons)
    {
        bool inside = false;
        foreach (var poly in polygons)
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var pi = poly[i]; var pj = poly[j];
                if ((pi.Y > p.Y) != (pj.Y > p.Y) &&
                    p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                    inside = !inside;
            }
        return inside;
    }

    /// <summary>Shortest distance between segments ab and cd.</summary>
    public static double SegmentDistance(CamPoint a, CamPoint b, CamPoint c, CamPoint d)
    {
        if (SegmentsIntersect(a, b, c, d)) return 0;
        return Math.Min(Math.Min(PointSegment(a, c, d), PointSegment(b, c, d)),
                        Math.Min(PointSegment(c, a, b), PointSegment(d, a, b)));
    }

    private static double PointSegment(CamPoint p, CamPoint a, CamPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        double t = len2 <= 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double x = a.X + dx * t - p.X, y = a.Y + dy * t - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    private static bool SegmentsIntersect(CamPoint a, CamPoint b, CamPoint c, CamPoint d)
    {
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(CamPoint o, CamPoint a, CamPoint b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    private static (double, double, double, double) Bounds(IEnumerable<CamPoint> points)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in points) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        return (x0, y0, x1, y1);
    }
}
