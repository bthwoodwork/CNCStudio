using System;
using System.Collections.Generic;

namespace CNCStudio.Core;

/// <summary>
/// Drilling (as in Easel): the tool plunges straight down at the centre of the shape. The hole gets the
/// tool's diameter. Each pass depth is one peck: the tool goes down by "Djup per pass", lifts out of the
/// hole to clear the chips, and goes back down. GRBL 1.1 has no canned drilling cycles (G81/G83), so
/// the motion planner writes plain G0/G1 moves.
/// </summary>
public static class DrillToolpaths
{
    /// <summary>How far the shape's size may differ from the tool diameter before it is reported.</summary>
    public const double SizeTolerance = 0.1;

    public static ContourPreview Generate(DesignShape shape, CamOperation op)
    {
        if (!double.IsFinite(shape.X) || !double.IsFinite(shape.Y) || !double.IsFinite(shape.Width) ||
            !double.IsFinite(shape.Height) || shape.Width <= 0 || shape.Height <= 0)
            throw new ArgumentException("Ogiltig objektgeometri.");
        var centre = Centre(shape);
        var depths = new List<double>();
        for (double d = op.DepthPerPass; d < op.TotalDepth - 1e-8; d += op.DepthPerPass) depths.Add(Math.Round(d, 6));
        depths.Add(op.TotalDepth);
        return new ContourPreview(new[] { centre }, depths,
            $"Borrning: Ø{op.ToolDiameter:0.###} mm, {op.TotalDepth:0.###} mm djupt i {depths.Count} steg.")
        { IsDrill = true };
    }

    /// <summary>The hole is drilled at the centre of the shape's bounding box.</summary>
    public static CamPoint Centre(DesignShape shape) => new(shape.X + shape.Width / 2, shape.Y + shape.Height / 2);

    /// <summary>True when the shape is (about) a circle of the tool's diameter – the hole it describes.</summary>
    public static bool MatchesTool(DesignShape shape, CamOperation op) =>
        Math.Abs(shape.Width - op.ToolDiameter) <= SizeTolerance && Math.Abs(shape.Height - op.ToolDiameter) <= SizeTolerance;
}
