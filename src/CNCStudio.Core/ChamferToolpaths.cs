using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// Fasning med V-fräs: the V-bit's tip follows the shape's outline at the depth that gives the chamfer width asked for
/// – width = depth × tan(half the bit's angle), so a 90° bit cuts a 45° chamfer as wide as it is deep. Half the V lies
/// on each side of the edge: the part gets the chamfer, the other side is waste (or air once the part is cut out).
/// <para>Used on its own (bearbetningen Fasning) or as a pass before an outside/inside contour or pocket
/// (<see cref="CamOperation.ChamferEnabled"/>), cut while the part is still held.</para>
/// </summary>
public static class ChamferToolpaths
{
    /// <summary>The depth that gives <paramref name="widthMm"/> of chamfer with a bit of <paramref name="angleDeg"/>.</summary>
    public static double Depth(double widthMm, double angleDeg) => widthMm / Math.Tan(angleDeg / 2 * Math.PI / 180);

    /// <summary>Why a chamfer cannot be cut with this bit, or null. The flank must reach the whole width.</summary>
    public static string? Validate(double widthMm, double toolDiameter, double angleDeg)
    {
        if (!double.IsFinite(widthMm) || widthMm <= 0) return "Fasbredden ska vara större än 0.";
        if (VCarveToolpaths.ValidateAngle(angleDeg) is string a) return a;
        if (!double.IsFinite(toolDiameter) || toolDiameter <= 0) return "Ange V-fräsens diameter.";
        if (widthMm > toolDiameter / 2 + 1e-9)
            return $"Fasen ({widthMm:0.##} mm) är bredare än V-fräsens flank räcker (högst halva diametern, {toolDiameter / 2:0.##} mm). Välj en större V-fräs eller en smalare fas.";
        return null;
    }

    public static ContourPreview Generate(DesignShape shape, CamOperation op)
    {
        var paths = Outline(shape);
        if (paths.Count == 0) throw new ArgumentException("Formen har ingen kant att fasa.");
        var depths = new List<double>();
        double step = op.DepthPerPass > 0 ? op.DepthPerPass : op.TotalDepth;
        for (double d = step; d < op.TotalDepth - 1e-8; d += step) depths.Add(Math.Round(d, 6));
        depths.Add(op.TotalDepth);
        return new ContourPreview(paths[0], depths,
            $"Fasning: V-fräsens spets längs kanten, {op.TotalDepth:0.##} mm djupt.")
        { Centerlines = paths };
    }

    /// <summary>The shape's edges as closed paths in mm (design coordinates).</summary>
    public static IReadOnlyList<IReadOnlyList<CamPoint>> Outline(DesignShape s)
    {
        var result = new List<IReadOnlyList<CamPoint>>();
        if (s.Kind == ShapeKind.Rectangle || s.Model is not null)
        {
            result.Add(new List<CamPoint> { new(s.X, s.Y), new(s.X + s.Width, s.Y), new(s.X + s.Width, s.Y + s.Height), new(s.X, s.Y + s.Height), new(s.X, s.Y) });
        }
        else if (s.Kind == ShapeKind.Ellipse)
        {
            double a = s.Width / 2, b = s.Height / 2, cx = s.X + a, cy = s.Y + b;
            int n = Math.Clamp((int)Math.Ceiling(Math.PI * (a + b) / 0.5), 72, 4096);
            var p = new List<CamPoint>(n + 1);
            for (int i = 0; i <= n; i++) { double t = 2 * Math.PI * i / n; p.Add(new CamPoint(cx + a * Math.Cos(t), cy + b * Math.Sin(t))); }
            result.Add(p);
        }
        else if (s.Contours is not null)
        {
            foreach (var c in s.Contours.Where(c => c.Points is { Count: >= 2 }))
            {
                var p = c.Points.Select(v => new CamPoint(s.X + v.X * s.Width, s.Y + v.Y * s.Height)).ToList();
                if (c.Closed && PathMath.Distance(p[0], p[^1]) > 1e-6) p.Add(p[0]);
                result.Add(p);
            }
        }
        return result;
    }
}
