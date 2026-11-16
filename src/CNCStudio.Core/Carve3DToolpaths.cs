using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// 3D-fräsning från STL: raster passes along X over the model's height map, the tool's
/// tip always at the height where its flat or round end just touches the surface (no gouging). Material outside the
/// shape counts as untouched, so the tool never cuts outside it (its edge gets the tool's radius).
/// <list type="bullet">
/// <item>Roughing (a 3D operation with a second tool): level by level, depth per pass deep, leaving
/// <see cref="RoughAllowanceMm"/> on the surface; only where there is material left on that level (lifting between).</item>
/// <item>Finishing (the second tool, usually a ball nose, or the only tool): one zig-zag over the whole model following
/// the surface, small stepover.</item>
/// </list>
/// Paths have a Z per point (<see cref="ContourPreview.PathZ"/>).
/// </summary>
public static class Carve3DToolpaths
{
    public const double RoughAllowanceMm = 0.5;
    private const int MaxPoints = 3_000_000;

    public static ContourPreview Generate(DesignShape shape, CamOperation op, double thickness)
    {
        var model = shape.Model ?? throw new ArgumentException("3D-fräsning kräver en 3D-modell (Rita → 3D-modell från STL…).");
        if (op.ToolKind == ToolKind.VBit) throw new ArgumentException("3D-fräsning görs med rak fräs eller kulfräs, inte V-fräs.");
        if (model.DepthMm > thickness + 1e-9) throw new ArgumentException($"Modellens djup ({model.DepthMm:0.##} mm) är större än materialet ({thickness:0.##} mm).");
        var map = Heightmap.Build(shape);
        double r = op.ToolDiameter / 2;
        bool ball = op.ToolKind == ToolKind.BallNose;
        bool finish = op.Kind == CamOperationKind.Carve3DFinish || (op.Kind == CamOperationKind.Carve3D && !op.SecondToolEnabled);
        double allowance = op.Kind == CamOperationKind.Carve3D && op.SecondToolEnabled ? RoughAllowanceMm : 0;
        double step = Math.Max(op.ToolDiameter * op.StepoverPercent / 100, 0.05);
        double sample = Math.Max(map.Cell, 0.1);

        // The tool's tip height at (x, y): the highest point under its end, lowered by the end's shape.
        int reach = (int)Math.Ceiling(r / map.Cell);
        var kernel = new List<(int Di, int Dj, double Drop)>();
        for (int dj = -reach; dj <= reach; dj++)
            for (int di = -reach; di <= reach; di++)
            {
                double d = Math.Sqrt(di * di + dj * dj) * map.Cell;
                if (d > r + 1e-9) continue;
                kernel.Add((di, dj, ball ? r - Math.Sqrt(Math.Max(r * r - d * d, 0)) : 0));
            }
        double Tip(double x, double y)
        {
            int ci = (int)Math.Floor((x - map.X0) / map.Cell), cj = (int)Math.Floor((y - map.Y0) / map.Cell);
            double best = double.MinValue;
            foreach (var (di, dj, drop) in kernel)
            {
                int i = ci + di, j = cj + dj;
                double h = i < 0 || j < 0 || i >= map.Nx || j >= map.Ny ? 0 : map.Z[j * map.Nx + i];
                if (h - drop > best) best = h - drop;
            }
            return Math.Min(best, 0);
        }

        double x0 = shape.X, x1 = shape.X + shape.Width, y0 = shape.Y, y1 = shape.Y + shape.Height;
        var rows = new List<double>();
        for (double y = y0; y < y1 + 1e-9; y += step) rows.Add(Math.Min(y, y1));
        if (rows[^1] < y1 - 1e-6) rows.Add(y1);
        var xs = new List<double>();
        for (double x = x0; x < x1 + 1e-9; x += sample) xs.Add(Math.Min(x, x1));
        if (xs[^1] < x1 - 1e-6) xs.Add(x1);
        var tips = rows.Select(y => xs.Select(x => Tip(x, y)).ToArray()).ToList();

        var paths = new List<IReadOnlyList<CamPoint>>();
        var zs = new List<IReadOnlyList<double>>();
        int total = 0;
        void Add(List<(double X, double Y, double Z)> pts)
        {
            var s = Simplify(pts);
            if (s.Count < 2) return;
            total += s.Count;
            if (total > MaxPoints) throw new NotSupportedException("För många punkter – öka stegningen eller minska modellen.");
            paths.Add(s.Select(p => new CamPoint(p.X, p.Y)).ToList());
            zs.Add(s.Select(p => p.Z).ToList());
        }

        var levels = new List<double>();
        if (finish && op.Kind == CamOperationKind.Carve3DFinish) levels.Add(model.DepthMm);
        else { for (double d = op.DepthPerPass; d < model.DepthMm - 1e-9; d += op.DepthPerPass) levels.Add(d); levels.Add(model.DepthMm); }

        if (finish && levels.Count == 1)
        {
            // One zig-zag over everything, the step between rows following the surface too.
            var pts = new List<(double, double, double)>();
            for (int k = 0; k < rows.Count; k++)
            {
                var order = k % 2 == 0 ? Enumerable.Range(0, xs.Count) : Enumerable.Range(0, xs.Count).Reverse();
                foreach (int i in order) pts.Add((xs[i], rows[k], tips[k][i] + allowance));
                if (k + 1 < rows.Count)
                {
                    double xe = k % 2 == 0 ? xs[^1] : xs[0];
                    for (double y = rows[k] + sample; y < rows[k + 1] - 1e-9; y += sample) pts.Add((xe, y, Tip(xe, y) + allowance));
                }
            }
            Add(pts);
        }
        else
        {
            double previous = 0;
            foreach (double depth in levels)
            {
                double floor = -depth;
                for (int k = 0; k < rows.Count; k++)
                {
                    var run = new List<(double, double, double)>();
                    var idx = k % 2 == 0 ? Enumerable.Range(0, xs.Count).ToList() : Enumerable.Range(0, xs.Count).Reverse().ToList();
                    foreach (int i in idx)
                    {
                        double z = Math.Max(floor, tips[k][i] + allowance);
                        if (z < -previous - 1e-6) run.Add((xs[i], rows[k], z));
                        else if (run.Count > 0) { Add(run); run = new(); }
                    }
                    Add(run);
                }
                previous = depth;
            }
        }
        if (paths.Count == 0) throw new ArgumentException("Inget att fräsa – modellen är platt eller djupet 0.");
        string warn = op.Kind == CamOperationKind.Carve3D && op.SecondToolEnabled
            ? $"Grovfräsning lämnar {RoughAllowanceMm:0.#} mm till finfräsningen."
            : finish && !ball ? "Rak fräs för finfräsning ger trappsteg i sluttningar – en kulfräs ger jämnare yta." : "";
        return new ContourPreview(paths[0], levels, warn) { Centerlines = paths, PathZ = zs, Is3D = true };
    }

    /// <summary>Removes points on a straight 3D line (within 0.01 mm), keeping every corner of the surface.</summary>
    private static List<(double X, double Y, double Z)> Simplify(List<(double X, double Y, double Z)> p)
    {
        if (p.Count < 3) return p;
        var res = new List<(double, double, double)> { p[0] };
        for (int i = 1; i < p.Count - 1; i++)
        {
            var a = res[^1]; var b = p[i]; var c = p[i + 1];
            double dx = c.X - a.Item1, dy = c.Y - a.Item2, dz = c.Z - a.Item3, len2 = dx * dx + dy * dy + dz * dz;
            if (len2 < 1e-12) continue;
            double t = ((b.X - a.Item1) * dx + (b.Y - a.Item2) * dy + (b.Z - a.Item3) * dz) / len2;
            double ex = a.Item1 + t * dx - b.X, ey = a.Item2 + t * dy - b.Y, ez = a.Item3 + t * dz - b.Z;
            if (ex * ex + ey * ey + ez * ez > 1e-4 || t < 0 || t > 1) res.Add(b);
        }
        res.Add(p[^1]);
        return res;
    }
}
