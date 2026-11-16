using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// One tab on a closed toolpath, as an arc-length interval [Start, End] in mm along
/// path <see cref="PathIndex"/>, measured from the path's first point.
/// TopDepth is the cut depth (mm below the stock top) at which the tool must lift over the tab.
/// </summary>
public sealed record TabSpan(int PathIndex, double Start, double End, double TopDepth)
{
    public double Length => End - Start;
}

/// <summary>Arc-length helpers for polyline toolpaths.</summary>
public static class PathMath
{
    public static double Length(IReadOnlyList<CamPoint> path)
    {
        double sum = 0;
        for (int i = 1; i < path.Count; i++) sum += Distance(path[i - 1], path[i]);
        return sum;
    }

    public static double Distance(CamPoint a, CamPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Point at arc length <paramref name="s"/> (clamped to the path).</summary>
    public static CamPoint PointAt(IReadOnlyList<CamPoint> path, double s)
    {
        if (path.Count == 0) throw new ArgumentException("Tom bana.");
        if (s <= 0) return path[0];
        double run = 0;
        for (int i = 1; i < path.Count; i++)
        {
            double len = Distance(path[i - 1], path[i]);
            if (run + len >= s && len > 0)
            {
                double t = (s - run) / len;
                return new CamPoint(path[i - 1].X + (path[i].X - path[i - 1].X) * t,
                                    path[i - 1].Y + (path[i].Y - path[i - 1].Y) * t);
            }
            run += len;
        }
        return path[^1];
    }

    /// <summary>
    /// The polyline between arc lengths s0 and s1 (s0 &lt; s1), including the vertices in between.
    /// For a closed path, s1 beyond the total length wraps around through the start.
    /// </summary>
    public static List<CamPoint> Slice(IReadOnlyList<CamPoint> path, double s0, double s1)
    {
        double total = Length(path);
        var result = new List<CamPoint>();
        if (total <= 0 || s1 <= s0) return result;
        if (s1 > total + 1e-9)
        {
            result.AddRange(Slice(path, s0, total));
            var rest = Slice(path, 0, s1 - total);
            result.AddRange(rest.Skip(result.Count > 0 && rest.Count > 0 && Distance(result[^1], rest[0]) < 1e-9 ? 1 : 0));
            return result;
        }
        result.Add(PointAt(path, s0));
        double run = 0;
        for (int i = 1; i < path.Count; i++)
        {
            run += Distance(path[i - 1], path[i]);
            if (run > s0 + 1e-9 && run < s1 - 1e-9) result.Add(path[i]);
        }
        result.Add(PointAt(path, s1));
        return result;
    }
}

/// <summary>
/// Places holding tabs on the closed paths of a contour operation.
/// Tabs are only applied to through cuts; in a partial-depth cut the part cannot come loose.
/// This is a plan for the future G-code: the XY path itself is unchanged and the tool
/// lifts to <see cref="TabSpan.TopDepth"/> over each tab.
/// </summary>
public static class CamTabs
{
    /// <summary>Minimum uncut path between neighbouring tabs, as a multiple of the tab's own length.</summary>
    private const double MinGapFactor = 1.0;

    public static bool AppliesTo(CamOperation op, double materialThickness) =>
        op.UseTabs && op.OnionSkinMm <= 0 && op.Kind is CamOperationKind.Outside or CamOperationKind.Inside &&
        op.TotalDepth >= materialThickness - 1e-6;

    /// <summary>
    /// Centerline length of one tab: the bridge width at the part plus the tool diameter,
    /// since the tool removes half its diameter on each side of the centerline.
    /// </summary>
    public static double CenterlineLength(CamOperation op) => op.TabWidth + op.ToolDiameter;

    /// <summary>The point on the paths nearest to (x, y): which path, the arc length there and the distance.</summary>
    public static (int Path, double S, double Distance) Nearest(IReadOnlyList<IReadOnlyList<CamPoint>> paths, double x, double y)
    {
        (int, double, double) best = (-1, 0, double.MaxValue);
        for (int pi = 0; pi < paths.Count; pi++)
        {
            var p = paths[pi];
            double run = 0;
            for (int i = 1; i < p.Count; i++)
            {
                var a = p[i - 1]; var b = p[i];
                double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy, len = Math.Sqrt(len2);
                double t = len2 < 1e-12 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
                double qx = a.X + dx * t - x, qy = a.Y + dy * t - y, d = Math.Sqrt(qx * qx + qy * qy);
                if (d < best.Item3) best = (pi, run + len * t, d);
                run += len;
            }
        }
        return best;
    }

    public static IReadOnlyList<TabSpan> Plan(ContourPreview preview, CamOperation op, double materialThickness, DesignShape? shape = null)
    {
        if (!AppliesTo(op, materialThickness)) return Array.Empty<TabSpan>();
        var paths = preview.Centerlines.Count > 0 ? preview.Centerlines :
            new IReadOnlyList<CamPoint>[] { preview.Centerline };
        double span = CenterlineLength(op);
        double top = Math.Max(0, op.TotalDepth - op.TabHeight);
        var tabs = new List<TabSpan>();
        if (op.ManualTabs is { Count: > 0 } manual && shape is not null)
        {
            // Hand-placed: each on the path point nearest to where it was put; one that would overlap a tab
            // already placed (or not fit on its path) is left out.
            foreach (var m in manual)
            {
                var (pi, s, _) = Nearest(paths, shape.X + m.X, shape.Y + m.Y);
                if (pi < 0) continue;
                double perimeter = PathMath.Length(paths[pi]);
                if (perimeter < span * (1 + MinGapFactor)) continue;
                // Kept within the path (a tab right at the start point moves along a little) – the motion
                // planner expects every tab between 0 and the path's length.
                double start = Math.Clamp(s - span / 2, 0, perimeter - span), end = start + span;
                bool overlaps = tabs.Any(t => t.PathIndex == pi && Math.Max(start - t.End, t.Start - end) < span * MinGapFactor);
                if (!overlaps) tabs.Add(new TabSpan(pi, start, end, top));
            }
            return tabs;
        }
        for (int pi = 0; pi < paths.Count; pi++)
        {
            double perimeter = PathMath.Length(paths[pi]);
            // Fit as many of the requested tabs as the perimeter allows.
            int fit = (int)Math.Floor(perimeter / (span * (1 + MinGapFactor)));
            int n = Math.Min(op.TabCount, fit);
            if (n <= 0) continue;
            double spacing = perimeter / n;
            for (int k = 0; k < n; k++)
            {
                // Centre each tab in its share of the perimeter, away from the start point.
                double centre = spacing * (k + 0.5);
                tabs.Add(new TabSpan(pi, centre - span / 2, centre + span / 2, top));
            }
        }
        return tabs;
    }

    /// <summary>The centres of the tabs as points in mm from the shape's top-left corner – to turn them into hand-placed tabs.</summary>
    public static List<TabPoint> Centres(ContourPreview preview, IReadOnlyList<TabSpan> tabs, DesignShape shape)
    {
        var paths = preview.Centerlines.Count > 0 ? preview.Centerlines : new IReadOnlyList<CamPoint>[] { preview.Centerline };
        return tabs.Where(t => t.PathIndex < paths.Count).Select(t =>
        {
            var path = paths[t.PathIndex];
            var p = PathMath.PointAt(path, (t.Start + t.End) / 2);
            return new TabPoint(Math.Round(p.X - shape.X, 3), Math.Round(p.Y - shape.Y, 3));
        }).ToList();
    }
}
