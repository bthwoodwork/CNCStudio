using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// Conservative XY-only review. Coordinates in preview paths are stock-local;
/// fixtures and machine dimensions are in the normalized machine view.
/// This does NOT verify Z clearance, the tool holder, rapids or physical setup.
/// </summary>
public static class CamSafetyReview
{
    /// <summary>
    /// TStart/TEnd (0..1) give the conflicting portion of the segment, so the UI
    /// marks only that part red. Issues that are not localized use 0..1.
    /// </summary>
    public sealed record Issue(string Code, string Description, Guid ShapeId, Guid? FixtureId,
        int PathIndex, int SegmentIndex, double TStart = 0, double TEnd = 1);

    public static IReadOnlyList<Issue> Analyze(
        CncDocument document, DesignShape shape, CamOperation operation, ContourPreview preview)
    {
        var result = new List<Issue>();
        var paths = preview.Centerlines.Count > 0 ? preview.Centerlines :
            new IReadOnlyList<CamPoint>[] { preview.Centerline };
        double toolRadius = operation.ToolDiameter / 2.0;
        if (!double.IsFinite(toolRadius) || toolRadius <= 0) return result;
        for (int pi = 0; pi < paths.Count; pi++)
        {
            var path = paths[pi];
            if (path.Count < 2) continue;
            for (int si = 1; si < path.Count; si++)
            {
                var a = path[si - 1]; var b = path[si];
                if (!Valid(a) || !Valid(b))
                {
                    result.Add(new Issue("InvalidCoordinate", "Ogiltig koordinat i XY-bana.", shape.Id, null, pi, si - 1));
                    continue;
                }
                double ax = a.X + document.Stock.X, ay = a.Y + document.Stock.Y;
                double bx = b.X + document.Stock.X, by = b.Y + document.Stock.Y;
                // A tool's *entire diameter* must remain inside the machine envelope.
                double minX = toolRadius, minY = toolRadius;
                double maxX = document.Machine.WorkWidthMm - toolRadius;
                double maxY = document.Machine.WorkLengthMm - toolRadius;
                if (!Inside(ax, ay, minX, minY, maxX, maxY) ||
                    !Inside(bx, by, minX, minY, maxX, maxY))
                    result.Add(new Issue("OutsideMachine", "Verktygets svepta XY-område kan gå utanför maskinens planerade arbetsyta.",
                        shape.Id, null, pi, si - 1));

                foreach (var f in document.Fixtures)
                {
                    // Inflation by tool radius and fixture-specific clearance is
                    // deliberately conservative around square fixture corners.
                    double inflate = toolRadius + f.ClearanceMm;
                    if (SegmentRectInterval(ax, ay, bx, by,
                        f.X - inflate, f.Y - inflate,
                        f.X + f.Width + inflate, f.Y + f.Length + inflate, out var t0, out var t1))
                        result.Add(new Issue("FixtureProximity",
                            $"XY-banan passerar nära fastspänningen '{f.Name}' (Ø {operation.ToolDiameter:0.###} mm, marginal {f.ClearanceMm:0.###} mm, höjd {f.HeightAboveStockMm:0.###} mm).",
                            shape.Id, f.Id, pi, si - 1, t0, t1));
                }
            }
        }
        return result;
    }

    private static bool Valid(CamPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    private static bool Inside(double x, double y, double left, double top, double right, double bottom) =>
        x >= left && x <= right && y >= top && y <= bottom;

    /// <summary>Line segment against an axis-aligned rectangle (inclusive, Liang-Barsky).</summary>
    public static bool SegmentIntersectsRect(double x0, double y0, double x1, double y1,
        double left, double top, double right, double bottom) =>
        SegmentRectInterval(x0, y0, x1, y1, left, top, right, bottom, out _, out _);

    /// <summary>
    /// Like <see cref="SegmentIntersectsRect"/> but also returns the parameter
    /// interval [t0, t1] of the segment that lies inside the rectangle.
    /// Invalid numbers fail closed: the whole segment (0..1) is reported.
    /// </summary>
    public static bool SegmentRectInterval(double x0, double y0, double x1, double y1,
        double left, double top, double right, double bottom, out double t0, out double t1)
    {
        t0 = 0; t1 = 1;
        if (!(double.IsFinite(x0) && double.IsFinite(y0) && double.IsFinite(x1) && double.IsFinite(y1) &&
              double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(right) && double.IsFinite(bottom)))
            return true; // fail closed on invalid numbers
        double dx = x1 - x0, dy = y1 - y0;
        return Clip(-dx, x0 - left, ref t0, ref t1) &&
               Clip(dx, right - x0, ref t0, ref t1) &&
               Clip(-dy, y0 - top, ref t0, ref t1) &&
               Clip(dy, bottom - y0, ref t0, ref t1);
    }

    private static bool Clip(double p, double q, ref double t0, ref double t1)
    {
        if (Math.Abs(p) < 1e-12) return q >= 0;
        double r = q / p;
        if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
        else { if (r < t0) return false; if (r < t1) t1 = r; }
        return true;
    }
}
