using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>One probed point: where (stock-local design X/Y, mm) and the surface height there (MPos Z).</summary>
public sealed record SurfacePoint(double X, double Y, double Z);

public sealed record FlatnessReport(double RangeMm, double TiltPer100Mm, double WarpMm, SurfacePoint Highest, SurfacePoint Lowest,
    string Verdict, IReadOnlyList<string> Advice, bool Ok);

/// <summary>
/// Planhetskontroll: a few points probed with the touch plate (guided: the plate is moved to each point) tell how
/// uneven the stock is where the job cuts – before a V-carving or inlay, whose width and fit follow the depth.
/// The points are fitted with a plane: what the plane explains is tilt (the board or the wasteboard leans), what is
/// left is warp (a cupped or twisted board).
/// </summary>
public static class Flatness
{
    /// <summary>Below this the surface counts as flat for any job.</summary>
    public const double FlatMm = 0.15;
    /// <summary>Above this, pockets and contours get visibly uneven depth too.</summary>
    public const double RoughMm = 0.5;

    /// <summary>
    /// Where to probe in the area (stock-local, Y down): 5 points (corners and middle) or 9 (a 3 × 3 grid), kept
    /// <paramref name="insetMm"/> in from the edges so the plate lies on the stock.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Points(double x0, double y0, double x1, double y1, int count, double insetMm = 15)
    {
        if (x1 - x0 < 1 || y1 - y0 < 1) throw new ArgumentException("Ytan är för liten.");
        double ix = Math.Min(insetMm, (x1 - x0) / 4), iy = Math.Min(insetMm, (y1 - y0) / 4);
        double ax = x0 + ix, bx = x1 - ix, ay = y0 + iy, by = y1 - iy, mx = (ax + bx) / 2, my = (ay + by) / 2;
        if (count <= 5)
            return new[] { (ax, ay), (bx, ay), (mx, my), (ax, by), (bx, by) };
        var list = new List<(double, double)>();
        foreach (var y in new[] { ay, my, by })
            foreach (var x in y == my ? new[] { ax, mx, bx } : new[] { ax, mx, bx })
                list.Add((x, y));
        // Snake order: fewer long moves between points.
        return new[] { list[0], list[1], list[2], list[5], list[4], list[3], list[6], list[7], list[8] };
    }

    /// <param name="vBitAngleDeg">The V-bit used in the project, if any: the report says how much the width varies.</param>
    public static FlatnessReport Analyze(IReadOnlyList<SurfacePoint> points, double? vBitAngleDeg = null)
    {
        if (points.Count < 3) throw new ArgumentException("Minst tre punkter behövs.");
        var hi = points.MaxBy(p => p.Z)!; var lo = points.MinBy(p => p.Z)!;
        double range = hi.Z - lo.Z;
        var (a, b, c) = FitPlane(points);
        double warp = points.Max(p => Math.Abs(p.Z - (a + b * p.X + c * p.Y)));
        double tilt = Math.Sqrt(b * b + c * c) * 100;

        var advice = new List<string>();
        string verdict;
        bool ok;
        if (range <= FlatMm) { verdict = $"Plant: {F(range)} mm skillnad mellan högsta och lägsta punkt."; ok = true; }
        else if (range <= RoughMm) { verdict = $"Nästan plant: {F(range)} mm skillnad."; ok = true; }
        else { verdict = $"Ojämnt: {F(range)} mm skillnad."; ok = false; }

        if (range > FlatMm)
        {
            if (vBitAngleDeg is double ang)
            {
                double widthChange = 2 * range * Math.Tan(ang * Math.PI / 360);
                advice.Add($"V-carving/inlay med {F(ang, 0)}° V-fräs: spåren blir upp till {F(widthChange)} mm smalare där ytan ligger lägst" +
                    (widthChange > 0.3 ? " – det syns, och en inlay passar sämre." : " – knappt synligt."));
            }
            if (warp <= range * 0.4 && tilt > 0.05)
                advice.Add($"Mest lutning ({F(tilt)} mm per 100 mm), lite buktighet ({F(warp)} mm): materialet ligger snett – kontrollera att inget ligger under det, eller planfräs offerskivan (Maskin → Planfräsa offerskivan…).");
            else
                advice.Add($"Materialet är buktigt eller skevt ({F(warp)} mm från ett plan): hyvla eller planfräs ytan först, eller spänn fast det plant (fler fästen mitt på).");
            if (range > RoughMm) advice.Add("Även fickor och konturer får ojämnt djup. Planfräs materialets yta en aning med en bred fräs, och nolla Z igen efteråt.");
            else advice.Add("För fickor och konturer räcker det; för grund gravyr och V-carving: överväg att planfräsa ytan.");
        }
        else advice.Add("Inget att göra – kör.");
        return new FlatnessReport(range, tilt, warp, hi, lo, verdict, advice, ok);
    }

    /// <summary>Least squares plane z = a + b·x + c·y.</summary>
    public static (double A, double B, double C) FitPlane(IReadOnlyList<SurfacePoint> p)
    {
        double n = p.Count, sx = p.Sum(q => q.X), sy = p.Sum(q => q.Y), sz = p.Sum(q => q.Z);
        double sxx = p.Sum(q => q.X * q.X), syy = p.Sum(q => q.Y * q.Y), sxy = p.Sum(q => q.X * q.Y);
        double sxz = p.Sum(q => q.X * q.Z), syz = p.Sum(q => q.Y * q.Z);
        // Solve [n sx sy; sx sxx sxy; sy sxy syy] [a b c] = [sz sxz syz] by Cramer's rule.
        double D(double a1, double a2, double a3, double b1, double b2, double b3, double c1, double c2, double c3) =>
            a1 * (b2 * c3 - b3 * c2) - a2 * (b1 * c3 - b3 * c1) + a3 * (b1 * c2 - b2 * c1);
        double det = D(n, sx, sy, sx, sxx, sxy, sy, sxy, syy);
        if (Math.Abs(det) < 1e-9) return (sz / n, 0, 0);       // points in a line: no plane
        double a = D(sz, sx, sy, sxz, sxx, sxy, syz, sxy, syy) / det;
        double b = D(n, sz, sy, sx, sxz, sxy, sy, syz, syy) / det;
        double c = D(n, sx, sz, sx, sxx, sxz, sy, sxy, syz) / det;
        return (a, b, c);
    }

    private static string F(double v, int d = 2) => Math.Round(v, d).ToString(d == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);
}
