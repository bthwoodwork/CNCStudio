using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.GCode;

public enum OutlineShape
{
    /// <summary>The program's bounding rectangle.</summary>
    Rectangle,
    /// <summary>The convex hull of the toolpath: the shape a rubber band around it would take.</summary>
    Hull
}

/// <summary>
/// Rita konturen (gSender's job outline): before a run, the tool goes round the outer edge of what the program
/// cuts – high above the material – so you can see it fits the stock and clears the clamps. Only cutting moves
/// count (rapids in the air do not, as gSender's "rapidless" outline). Z is first lifted near the top in machine
/// coordinates (the same height as Gå till X0 Y0), the outline is followed at the given feed, and the tool comes
/// back to where it started. Nothing goes down.
/// </summary>
public static class JobOutline
{
    /// <summary>The corners to follow (closed: the first corner is not repeated), in work coordinates. Empty when the program cuts nothing.</summary>
    public static IReadOnlyList<(double X, double Y)> Points(IReadOnlyList<ProgramMove> moves, OutlineShape shape)
    {
        var pts = moves.Where(m => !m.Rapid).SelectMany(m => new[] { (m.X0, m.Y0), (m.X1, m.Y1) }).Distinct().ToList();
        if (pts.Count == 0) return Array.Empty<(double, double)>();
        if (shape == OutlineShape.Rectangle)
        {
            double x0 = pts.Min(p => p.Item1), x1 = pts.Max(p => p.Item1), y0 = pts.Min(p => p.Item2), y1 = pts.Max(p => p.Item2);
            return new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };
        }
        return Hull(pts);
    }

    /// <summary>
    /// The outline as GRBL lines: lift Z in machine coordinates (<paramref name="safeMachineZ"/>, e.g. −$LimitMargin),
    /// go to the first corner, follow the outline back to it at <paramref name="feed"/>, then back to
    /// <paramref name="returnTo"/> (where the tool stood), all at the lifted height.
    /// </summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<(double X, double Y)> points, double safeMachineZ, double feed, (double X, double Y)? returnTo)
    {
        if (points.Count == 0) throw new ArgumentException("Programmet skär ingenting – det finns ingen kontur.");
        if (!(feed > 0)) throw new ArgumentException("Hastigheten måste vara större än 0.");
        var lines = new List<string> { $"G53 G0 Z{N(safeMachineZ)}", $"G90 G0 X{N(points[0].X)} Y{N(points[0].Y)}" };
        foreach (var p in points.Skip(1).Append(points[0]))
            lines.Add($"G1 X{N(p.X)} Y{N(p.Y)} F{N(feed, "0")}");
        if (returnTo is { } r) lines.Add($"G0 X{N(r.X)} Y{N(r.Y)}");
        return lines;
    }

    /// <summary>Andrew's monotone chain; counter-clockwise, no repeated first point.</summary>
    private static IReadOnlyList<(double X, double Y)> Hull(List<(double X, double Y)> pts)
    {
        var p = pts.OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        if (p.Count < 3) return p;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(double X, double Y)>();
        foreach (var pt in p) { while (hull.Count >= 2 && Cross(hull[^2], hull[^1], pt) <= 1e-12) hull.RemoveAt(hull.Count - 1); hull.Add(pt); }
        int lower = hull.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--) { while (hull.Count >= lower && Cross(hull[^2], hull[^1], p[i]) <= 1e-12) hull.RemoveAt(hull.Count - 1); hull.Add(p[i]); }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static string N(double v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);
}
