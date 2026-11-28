using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// Drilling. A round shape of the tool's size is drilled straight down at its centre: each pass depth is one
/// peck (down by "Djup per pass", lift out of the hole for the chips, back down). A round shape LARGER than the
/// tool is drilled with a spiral (helical boring, as Fusion's bore/helix): the tool goes round inside the hole
/// while it descends, "Djup per pass" per turn, then one flat turn at the bottom. A hole more than twice the tool
/// gets several spirals, inside out, each at most half the tool's diameter further out, so no core is left.
/// <para>GRBL 1.1 has no canned drilling cycles (G81/G83). The spiral is written as short G1 moves along the circle
/// with Z falling (as a ramped plunge) – no G2/G3 – so the G-code uses only the verified G0/G1 set.</para>
/// </summary>
public static class DrillToolpaths
{
    /// <summary>How far the shape's size may differ from the tool diameter before it is reported.</summary>
    public const double SizeTolerance = 0.1;
    /// <summary>The spiral's circle is drawn with this largest deviation from the true circle (mm).</summary>
    public const double ChordToleranceMm = 0.005;
    /// <summary>A spiral steeper than this (degrees from horizontal, on the outermost turn) is flagged.</summary>
    public const double SteepHelixDeg = 10;

    public static ContourPreview Generate(DesignShape shape, CamOperation op)
    {
        if (!double.IsFinite(shape.X) || !double.IsFinite(shape.Y) || !double.IsFinite(shape.Width) ||
            !double.IsFinite(shape.Height) || shape.Width <= 0 || shape.Height <= 0)
            throw new ArgumentException("Ogiltig objektgeometri.");
        if (HelixHoleDiameter(shape, op) is double hole) return Helix(shape, op, hole);
        var centre = Centre(shape);
        var depths = Depths(op.TotalDepth, op.DepthPerPass);
        return new ContourPreview(new[] { centre }, depths,
            $"Borrning: Ø{op.ToolDiameter:0.###} mm, {op.TotalDepth:0.###} mm djupt i {depths.Count} steg.")
        { IsDrill = true };
    }

    /// <summary>The hole is drilled at the centre of the shape's bounding box.</summary>
    public static CamPoint Centre(DesignShape shape) => new(shape.X + shape.Width / 2, shape.Y + shape.Height / 2);

    /// <summary>True when the shape is (about) a circle of the tool's diameter – the hole it describes.</summary>
    public static bool MatchesTool(DesignShape shape, CamOperation op) =>
        Math.Abs(shape.Width - op.ToolDiameter) <= SizeTolerance && Math.Abs(shape.Height - op.ToolDiameter) <= SizeTolerance;

    /// <summary>The hole's diameter when it is drilled with a spiral: a circle larger than the tool. Null = straight drilling.</summary>
    public static double? HelixHoleDiameter(DesignShape shape, CamOperation op)
    {
        if (op.Kind != CamOperationKind.Drill || !IsCircle(shape)) return null;
        double d = (shape.Width + shape.Height) / 2;
        return d > op.ToolDiameter + SizeTolerance ? d : null;
    }

    /// <summary>
    /// A circle: an ellipse with equal width and height, or an imported shape with one closed contour whose points all
    /// lie on a circle (within the size tolerance, or 2 % of the radius for a coarsely drawn one).
    /// </summary>
    public static bool IsCircle(DesignShape shape)
    {
        if (Math.Abs(shape.Width - shape.Height) > SizeTolerance) return false;
        if (shape.Kind == ShapeKind.Ellipse) return true;
        if (shape.Kind != ShapeKind.VectorPath || shape.Contours is not { Count: 1 } contours) return false;
        var c = contours[0];
        if (!c.Closed || c.Points.Count < 8) return false;
        var centre = Centre(shape);
        double r = (shape.Width + shape.Height) / 4, tol = Math.Max(SizeTolerance, r * 0.02);
        return c.Points.All(p =>
        {
            double x = shape.X + p.X * shape.Width - centre.X, y = shape.Y + p.Y * shape.Height - centre.Y;
            return Math.Abs(Math.Sqrt(x * x + y * y) - r) <= tol;
        });
    }

    /// <summary>The radii of the spirals' circles (tool centre), innermost first; the innermost reaches the centre.</summary>
    public static IReadOnlyList<double> HelixRadii(double holeDiameter, double toolDiameter)
    {
        double outer = (holeDiameter - toolDiameter) / 2;
        int n = Math.Max(1, (int)Math.Ceiling(outer / (toolDiameter / 2) - 1e-9));
        return Enumerable.Range(1, n).Select(i => outer * i / n).ToList();
    }

    /// <summary>The spiral's angle from horizontal on a circle of this radius, in degrees.</summary>
    public static double HelixAngleDeg(double radius, double depthPerTurn) =>
        Math.Atan2(depthPerTurn, 2 * Math.PI * radius) * 180 / Math.PI;

    private static ContourPreview Helix(DesignShape shape, CamOperation op, double hole)
    {
        var c = Centre(shape);
        var radii = HelixRadii(hole, op.ToolDiameter);
        // Equal turns, none deeper than "Djup per pass": e.g. 3.3 mm at 1 mm per turn = 4 turns of 0.825 mm.
        int turns = Math.Max(1, (int)Math.Ceiling(op.TotalDepth / op.DepthPerPass - 1e-9));
        var depths = Enumerable.Range(1, turns).Select(k => k == turns ? op.TotalDepth : Math.Round(op.TotalDepth * k / turns, 6)).ToList();
        var rings = radii.Select(r => (IReadOnlyList<CamPoint>)Circle(c, r)).ToList();
        string spirals = radii.Count == 1 ? "en spiral" : $"{radii.Count} spiraler inifrån och ut";
        return new ContourPreview(rings[0], depths,
            $"Spiralborrning: hål Ø{hole:0.###} mm med Ø{op.ToolDiameter:0.###} mm fräs, {spirals}, {turns} varv ned till {op.TotalDepth:0.###} mm och ett plant varv i botten.")
        { IsHelix = true, Centerlines = rings, HelixCentre = c, HelixHoleDiameter = hole };
    }

    /// <summary>
    /// A closed circle starting at the point to the right of the centre, counter-clockwise seen from above (machine
    /// coordinates, Y towards the back; the design's Y points the other way). With M3 that is climb milling: the wall
    /// stays on the tool's right.
    /// </summary>
    private static List<CamPoint> Circle(CamPoint c, double r)
    {
        double step = 2 * Math.Acos(Math.Max(-1, 1 - ChordToleranceMm / r));
        int n = Math.Clamp((int)Math.Ceiling(2 * Math.PI / step), 24, 720);
        var pts = new List<CamPoint>(n + 1);
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pts.Add(new CamPoint(c.X + r * Math.Cos(a), c.Y - r * Math.Sin(a)));
        }
        pts.Add(pts[0]);
        return pts;
    }

    private static List<double> Depths(double total, double perPass)
    {
        var depths = new List<double>();
        for (double d = perPass; d < total - 1e-8; d += perPass) depths.Add(Math.Round(d, 6));
        depths.Add(total);
        return depths;
    }
}
