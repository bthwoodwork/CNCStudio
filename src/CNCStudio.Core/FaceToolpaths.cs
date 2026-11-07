using System;
using System.Collections.Generic;

namespace CNCStudio.Core;

/// <summary>
/// Facing (planfräsning): the whole area of the shape (its bounding box) is cut flat, row by row in a zigzag.
/// Every row runs out past both edges by the tool radius plus <see cref="CamOperation.FaceOverhangMm"/>, so
/// the cutter leaves the material completely before it moves over to the next row; that also makes the
/// plunge happen in the air beside the material. Rows are spaced by the stepover; the last row lies on
/// the far edge so nothing is left there.
/// </summary>
public static class FaceToolpaths
{
    public static ContourPreview Generate(DesignShape shape, CamOperation op)
    {
        if (!double.IsFinite(shape.X) || !double.IsFinite(shape.Y) || !double.IsFinite(shape.Width) ||
            !double.IsFinite(shape.Height) || shape.Width <= 0 || shape.Height <= 0)
            throw new ArgumentException("Ogiltig objektgeometri.");
        double r = op.ToolDiameter / 2;
        double step = op.ToolDiameter * op.StepoverPercent / 100;
        double run = r + op.FaceOverhangMm;                         // beyond the edge along a row
        bool alongY = op.FaceAlongY;
        // Rows run along X (or Y); they are stacked across the other direction.
        double rowStart = alongY ? shape.Y - run : shape.X - run;
        double rowEnd = alongY ? shape.Y + shape.Height + run : shape.X + shape.Width + run;
        double across0 = alongY ? shape.X : shape.Y;
        double across1 = alongY ? shape.X + shape.Width : shape.Y + shape.Height;

        var rows = new List<double>();
        for (double a = across0; a < across1 - 1e-6; a += step) rows.Add(Math.Round(a, 6));
        rows.Add(across1);                                         // the far edge is always cut
        var path = new List<CamPoint>();
        bool forward = true;
        foreach (var a in rows)
        {
            double from = forward ? rowStart : rowEnd, to = forward ? rowEnd : rowStart;
            path.Add(alongY ? new CamPoint(a, from) : new CamPoint(from, a));
            path.Add(alongY ? new CamPoint(a, to) : new CamPoint(to, a));
            forward = !forward;
        }
        var depths = new List<double>();
        for (double d = op.DepthPerPass; d < op.TotalDepth - 1e-8; d += op.DepthPerPass) depths.Add(Math.Round(d, 6));
        depths.Add(op.TotalDepth);
        return new ContourPreview(path, depths,
            $"Planfräsning: {rows.Count} rader, stegning {step:0.##} mm, {depths.Count} pass.")
        { IsFace = true, Centerlines = new[] { (IReadOnlyList<CamPoint>)path } };
    }
}
