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
    /// Checks the tool's swept XY area against the machine envelope and every fixture.
    /// <list type="bullet">
    /// <item>Critical: path outside the machine, or the tool itself overlaps a fixture's footprint.</item>
    /// <item>Warning: the tool is within the fixture's own safety clearance.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<CamFinding> Analyze(
        CncDocument document, DesignShape shape, CamOperation operation, ContourPreview preview)
    {
        var result = new List<CamFinding>();
        var paths = preview.Centerlines.Count > 0 ? preview.Centerlines :
            new IReadOnlyList<CamPoint>[] { preview.Centerline };
        double toolRadius = operation.ToolDiameter / 2.0;
        if (!double.IsFinite(toolRadius) || toolRadius <= 0)
        {
            result.Add(new CamFinding(CamSeverity.Critical, "InvalidTool",
                "Ogiltig fräsdiameter – banan kan inte kontrolleras.", shape.Id));
            return result;
        }
        // A tool's *entire diameter* must remain inside the machine envelope, with the
        // machine profile's safety margin to the limit switches.
        double edge = toolRadius + Math.Max(0, document.Machine.LimitMarginMm);
        double minX = edge, minY = edge;
        double maxX = document.Machine.WorkWidthMm - edge;
        double maxY = document.Machine.WorkLengthMm - edge;

        for (int pi = 0; pi < paths.Count; pi++)
        {
            var path = paths[pi];
            if (path.Count < 2) continue;
            for (int si = 1; si < path.Count; si++)
            {
                var a = path[si - 1]; var b = path[si];
                if (!Valid(a) || !Valid(b))
                {
                    result.Add(new CamFinding(CamSeverity.Critical, "InvalidCoordinate",
                        "Ogiltig koordinat i XY-bana.", shape.Id, null, pi, si - 1));
                    continue;
                }
                double ax = a.X + document.Stock.X, ay = a.Y + document.Stock.Y;
                double bx = b.X + document.Stock.X, by = b.Y + document.Stock.Y;
                if (!Inside(ax, ay, minX, minY, maxX, maxY) || !Inside(bx, by, minX, minY, maxX, maxY))
                    result.Add(new CamFinding(CamSeverity.Critical, "OutsideMachine",
                        $"Verktygets svepta XY-område går utanför maskinens arbetsområde (inklusive {document.Machine.LimitMarginMm:0.##} mm marginal till gränslägena).",
                        shape.Id, null, pi, si - 1));

                foreach (var f in document.Fixtures)
                    AddFixtureFindings(result, f, shape.Id, operation, toolRadius, ax, ay, bx, by, pi, si - 1);
            }
        }
        return result;
    }

    private static void AddFixtureFindings(List<CamFinding> result, FixtureBlock f, Guid shapeId,
        CamOperation operation, double toolRadius,
        double ax, double ay, double bx, double by, int pathIndex, int segmentIndex)
    {
        // Outer zone: footprint + tool radius + the fixture's own clearance.
        double outer = toolRadius + Math.Max(0, f.ClearanceMm);
        if (!SegmentRectInterval(ax, ay, bx, by, f.X - outer, f.Y - outer,
                f.X + f.Width + outer, f.Y + f.Length + outer, out var w0, out var w1))
            return;
        // Inner zone: the tool's edge actually overlaps the fixture footprint.
        // Inflating a rectangle by the radius is conservative at its corners.
        bool hitsCore = SegmentRectInterval(ax, ay, bx, by, f.X - toolRadius, f.Y - toolRadius,
            f.X + f.Width + toolRadius, f.Y + f.Length + toolRadius, out var c0, out var c1);
        string details = $"(Ø {operation.ToolDiameter:0.###} mm, marginal {f.ClearanceMm:0.###} mm, höjd {f.HeightAboveStockMm:0.###} mm)";
        if (!hitsCore)
        {
            result.Add(new CamFinding(CamSeverity.Warning, "FixtureClearance",
                $"Banan går inom säkerhetsmarginalen för fastspänningen '{f.Name}' {details}.",
                shapeId, f.Id, pathIndex, segmentIndex, w0, w1));
            return;
        }
        result.Add(new CamFinding(CamSeverity.Critical, "FixtureCollision",
            $"Fräsen skär in i fastspänningen '{f.Name}' {details}.",
            shapeId, f.Id, pathIndex, segmentIndex, c0, c1));
        const double eps = 1e-9;
        if (c0 - w0 > eps)
            result.Add(new CamFinding(CamSeverity.Warning, "FixtureClearance",
                $"Banan går inom säkerhetsmarginalen för fastspänningen '{f.Name}' {details}.",
                shapeId, f.Id, pathIndex, segmentIndex, w0, c0));
        if (w1 - c1 > eps)
            result.Add(new CamFinding(CamSeverity.Warning, "FixtureClearance",
                $"Banan går inom säkerhetsmarginalen för fastspänningen '{f.Name}' {details}.",
                shapeId, f.Id, pathIndex, segmentIndex, c1, w1));
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
