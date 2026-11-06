using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace CNCStudio.Core;

/// <summary>
/// Pocket clearing with concentric offsets (Clipper2). The tool centre may go anywhere in the pocket
/// shrunk by the tool radius; loops are offset inwards by the stepover until nothing is left, and
/// cut from the inside out so the last loop is the finishing pass along the pocket wall.
/// Islands and holes in vector shapes are kept (even-odd).
/// </summary>
public static class PocketToolpaths
{
    private const int Precision = 4;          // decimal places used by Clipper2's double API
    private const double ArcTolerance = 0.01; // mm, max deviation when rounding offset corners
    private const int MaxLoops = 5000;
    private const int MaxPoints = 400_000;

    public static ContourPreview Generate(DesignShape shape, CamOperation op, double materialThickness)
    {
        if (op.Kind != CamOperationKind.Pocket) throw new ArgumentException("Inte en fickoperation.");
        if (CamValidation.GetError(op, materialThickness) is string problem) throw new ArgumentException(problem);
        if (!double.IsFinite(shape.X) || !double.IsFinite(shape.Y) || shape.Width <= 0 || shape.Height <= 0)
            throw new ArgumentException("Ogiltig objektgeometri.");
        if (shape.Kind == ShapeKind.VectorPath && (shape.Contours.Count == 0 || shape.Contours.Any(c => !c.Closed)))
            throw new ArgumentException("Fickfräsning kräver slutna konturer.");

        var region = Region(shape);
        if (Math.Abs(Clipper.Area(region)) < 1e-6) throw new ArgumentException("Fickan har ingen yta.");
        double r = op.ToolDiameter / 2, step = op.ToolDiameter * op.StepoverPercent / 100;

        var centre = Simplify(Offset(region, -r));
        if (centre.Count == 0)
            throw new ArgumentException($"Fräsen (Ø {op.ToolDiameter:0.###} mm) ryms inte i fickan.");
        var levels = new List<PathsD>();
        for (var level = centre; level.Count > 0; level = Simplify(Offset(level, -step)))
        {
            levels.Add(level);
            if (levels.Count > MaxLoops) throw new NotSupportedException("För många varv i fickan – öka stegningen.");
        }

        // Inside out: deepest offset first, the wall-following loop last. Within a level, go to the
        // nearest loop; each loop starts at its vertex nearest to where the previous one ended.
        var loops = new List<IReadOnlyList<CamPoint>>();
        CamPoint? at = null;
        int total = 0;
        for (int li = levels.Count - 1; li >= 0; li--)
        {
            var remaining = levels[li].Where(p => p.Count >= 3).ToList();
            while (remaining.Count > 0)
            {
                var here = at;
                var next = here is null ? remaining[0]
                    : remaining.OrderBy(p => p.Min(v => Dist2(v, here))).First();
                remaining.Remove(next);
                var loop = Rotate(next, here);
                total += loop.Count;
                if (total > MaxPoints) throw new NotSupportedException("För komplex ficka för förhandsvisning.");
                loops.Add(loop);
                at = loop[0];
            }
        }

        // Rest-material check with a coarser arc tolerance: it looks for gaps between loops, which are
        // far larger than 0.05 mm, and stays fast for big pockets.
        var reachable = Clipper.InflatePaths(centre, r, JoinType.Round, EndType.Polygon, 2.0, Precision, CheckArcTolerance);
        var swept = Clipper.Union(Clipper.InflatePaths(new PathsD(loops.Select(ToPath)), r,
            JoinType.Round, EndType.Joined, 2.0, Precision, CheckArcTolerance), FillRule.NonZero);
        double rest = Math.Abs(Clipper.Area(Clipper.Difference(reachable, swept, FillRule.NonZero, Precision)));
        double corners = Math.Max(0, Math.Abs(Clipper.Area(region)) - Math.Abs(Clipper.Area(reachable)));

        var depths = new List<double>();
        for (double d = op.DepthPerPass; d < op.TotalDepth - 1e-8; d += op.DepthPerPass) depths.Add(Math.Round(d, 6));
        depths.Add(op.TotalDepth);
        return new ContourPreview(loops[0], depths,
            $"Fickfräsning: {loops.Count} varv, stegning {op.StepoverPercent:0.#} %. Ingen rampning – rak nedmatning.")
        {
            Centerlines = loops,
            IsPocket = true,
            PocketCenterRegion = centre.Select(p => (IReadOnlyList<CamPoint>)p.Select(v => new CamPoint(v.x, v.y)).ToList()).ToList(),
            PocketRestArea = rest,
            PocketCornerArea = corners
        };
    }

    /// <summary>The pocket's filled region in stock-local mm (rectangle, ellipse or even-odd vector contours).</summary>
    public static PathsD Region(DesignShape s)
    {
        PathsD raw = new();
        switch (s.Kind)
        {
            case ShapeKind.Rectangle:
                raw.Add(new PathD { new(s.X, s.Y), new(s.X + s.Width, s.Y), new(s.X + s.Width, s.Y + s.Height), new(s.X, s.Y + s.Height) });
                break;
            case ShapeKind.Ellipse:
            {
                // Vertices on the ellipse: the polygon lies inside it, so a pocket never overcuts.
                double a = s.Width / 2, b = s.Height / 2, cx = s.X + a, cy = s.Y + b;
                int n = Math.Clamp((int)Math.Ceiling(Math.PI * (a + b) / 0.5), 72, 2000);
                var p = new PathD();
                for (int i = 0; i < n; i++) { double t = 2 * Math.PI * i / n; p.Add(new PointD(cx + a * Math.Cos(t), cy + b * Math.Sin(t))); }
                raw.Add(p);
                break;
            }
            default:
                foreach (var c in s.Contours.Where(c => c.Closed && c.Points.Count >= 3))
                    raw.Add(new PathD(c.Points.Select(v => new PointD(s.X + v.X * s.Width, s.Y + v.Y * s.Height))));
                break;
        }
        return Clipper.Union(raw, FillRule.EvenOdd);
    }

    /// <summary>
    /// True if the straight segment a→b keeps the tool centre inside the pocket's centre region,
    /// so the tool can feed along it at depth without touching the pocket wall or an island.
    /// </summary>
    public static bool SegmentInside(CamPoint a, CamPoint b, IReadOnlyList<IReadOnlyList<CamPoint>> region)
    {
        if (region.Count == 0) return false;
        // Endpoints may lie on the boundary (the finishing loop runs along it), but the segment must
        // not cross any boundary edge, and sample points along it must be inside or on the boundary.
        foreach (var poly in region)
            for (int i = 0; i < poly.Count; i++)
                if (ProperlyCross(a, b, poly[i], poly[(i + 1) % poly.Count])) return false;
        for (int k = 1; k <= 7; k++)
        {
            double t = k / 8.0;
            var p = new CamPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            if (!CamGeometryChecks.Inside(p, region) && DistanceToBoundary(p, region) > 1e-3) return false;
        }
        return true;
    }

    private static bool ProperlyCross(CamPoint a, CamPoint b, CamPoint c, CamPoint d)
    {
        static double Cross(CamPoint o, CamPoint p, CamPoint q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        const double eps = 1e-9;
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps));
    }

    private static double DistanceToBoundary(CamPoint p, IReadOnlyList<IReadOnlyList<CamPoint>> region)
    {
        double best = double.MaxValue;
        foreach (var poly in region)
            for (int i = 0; i < poly.Count; i++)
                best = Math.Min(best, CamGeometryChecks.SegmentDistance(p, p, poly[i], poly[(i + 1) % poly.Count]));
        return best;
    }

    private const double CheckArcTolerance = 0.05;
    /// <summary>Max deviation when removing redundant vertices – far below machine resolution.</summary>
    private const double SimplifyEpsilon = 0.005;

    private static PathsD Simplify(PathsD paths) =>
        new(Clipper.SimplifyPaths(paths, SimplifyEpsilon, true).Where(p => p.Count >= 3));

    private static PathsD Offset(PathsD paths, double delta) =>
        Clipper.InflatePaths(paths, delta, JoinType.Round, EndType.Polygon, 2.0, Precision, ArcTolerance);

    private static PathD ToPath(IReadOnlyList<CamPoint> loop) => new(loop.Select(p => new PointD(p.X, p.Y)));

    private static double Dist2(PointD v, CamPoint p) => (v.x - p.X) * (v.x - p.X) + (v.y - p.Y) * (v.y - p.Y);

    /// <summary>Closed loop starting at the vertex nearest <paramref name="near"/>; last point = first.</summary>
    private static List<CamPoint> Rotate(PathD path, CamPoint? near)
    {
        int start = 0;
        if (near is not null)
        {
            double best = double.MaxValue;
            for (int i = 0; i < path.Count; i++) { var d = Dist2(path[i], near); if (d < best) { best = d; start = i; } }
        }
        var loop = new List<CamPoint>(path.Count + 1);
        for (int i = 0; i < path.Count; i++) { var v = path[(start + i) % path.Count]; loop.Add(new CamPoint(v.x, v.y)); }
        loop.Add(loop[0]);
        return loop;
    }
}
