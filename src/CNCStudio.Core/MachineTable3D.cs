using System;
using System.Collections.Generic;

namespace CNCStudio.Core;

/// <summary>
/// Where the machine's work area lies in the 3D view. The 3D view is drawn in the material's own frame:
/// X to the right from the material's left edge, Y backwards (up on the screen in 2D) from its front edge,
/// Z up from the material's top. The machine table in 2D is drawn from its back-left corner with Y towards
/// the front, and <see cref="CncDocument.Stock"/> is the material's back-left corner on that table.
/// </summary>
public static class MachineTable3D
{
    /// <summary>The work area (X <paramref name="Left"/>..<paramref name="Right"/>, Y <paramref name="Front"/>..<paramref name="Back"/>) in the 3D view's frame, and the table top's height (the material's underside).</summary>
    public readonly record struct Area(double Left, double Right, double Front, double Back, double TopZ)
    {
        public double Width => Right - Left;
        public double Length => Back - Front;
    }

    /// <summary>The work area under the material. Without material the area starts at the view's origin.</summary>
    public static Area For(CncDocument doc)
    {
        double w = doc.Machine.WorkWidthMm, l = doc.Machine.WorkLengthMm;
        if (!doc.HasMaterial) return new Area(0, w, 0, l, 0);
        double sx = doc.Stock.X, sy = doc.Stock.Y, ml = doc.MaterialHeight;
        // Table-canvas y (down = front) to view y (up = back) through the material's frame: y_view = ml − (y_table − sy).
        return new Area(-sx, w - sx, ml - (l - sy), ml + sy, -doc.MaterialThickness);
    }

    /// <summary>
    /// A point on the 2D machine table (from its back-left corner, Y towards the front – where the tool
    /// marker is drawn) in the 3D view's frame.
    /// </summary>
    public static (double X, double Y) FromTable(Area a, double tableX, double tableY) => (a.Left + tableX, a.Back - tableY);

    /// <summary>The home corner (where the X/Y switches are) on the work area.</summary>
    public static (double X, double Y) HomePoint(Area a, HomeCorner corner) => corner switch
    {
        HomeCorner.BackLeft => (a.Left, a.Back),
        HomeCorner.BackRight => (a.Right, a.Back),
        HomeCorner.FrontLeft => (a.Left, a.Front),
        _ => (a.Right, a.Front)
    };

    /// <summary>Grid lines across the area every <see cref="GridStep"/> mm, counted from the home corner like the 2D machine view.</summary>
    public static IReadOnlyList<(double X0, double Y0, double X1, double Y1)> GridLines(Area a, HomeCorner corner)
    {
        double step = GridStep(a);
        var (hx, hy) = HomePoint(a, corner);
        var lines = new List<(double, double, double, double)>();
        for (double d = step; d < a.Width - 1e-6; d += step)
        {
            double x = hx == a.Left ? a.Left + d : a.Right - d;
            lines.Add((x, a.Front, x, a.Back));
        }
        for (double d = step; d < a.Length - 1e-6; d += step)
        {
            double y = hy == a.Front ? a.Front + d : a.Back - d;
            lines.Add((a.Left, y, a.Right, y));
        }
        return lines;
    }

    /// <summary>100 mm on a normal table, 50 mm on a small one.</summary>
    public static double GridStep(Area a) => Math.Min(a.Width, a.Length) < 400 ? 50 : 100;
}
