using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// Height map of the material after carving: one Z value per cell (0 = stock top, negative = cut).
/// Cells are in work coordinates with X to the right and Y towards the back, origin at the
/// material's front-left corner (independent of the chosen work zero).
/// </summary>
public sealed class StockHeightMap
{
    public StockHeightMap(int nx, int ny, double cell, double width, double length, double thickness)
    {
        Nx = nx; Ny = ny; Cell = cell; Width = width; Length = length; Thickness = thickness;
        Heights = new float[nx * ny];
    }

    public int Nx { get; }
    public int Ny { get; }
    /// <summary>Cell size in mm.</summary>
    public double Cell { get; }
    public double Width { get; }
    public double Length { get; }
    public double Thickness { get; }
    public float[] Heights { get; }

    public float this[int ix, int iy] => Heights[iy * Nx + ix];
    /// <summary>A cell cut through the whole material.</summary>
    public bool IsThrough(int ix, int iy) => this[ix, iy] <= -Thickness + 1e-4;
    public double MinHeight => Heights.Length == 0 ? 0 : Heights.Min();
    public double RemovedVolume => Heights.Sum(h => -(double)h) * Cell * Cell;
}

/// <summary>
/// Simulates material removal by stamping the tool shape along every cutting move of the motion
/// plan: flat end mill (flat disc), ball nose (hemisphere) or V-bit (cone). Rapids never cut
/// (they are always above the surface). A visual estimate – not a verification of the G-code.
/// </summary>
public static class StockSimulator
{
    /// <summary>Upper bound on cells, so big sheets get a coarser grid rather than a slow simulation.</summary>
    public const int MaxCells = 600_000;

    public static double CellSizeFor(double width, double length, double finest = 0.25) =>
        Math.Max(finest, Math.Sqrt(width * length / MaxCells));

    public static StockHeightMap Simulate(CncDocument doc, CamReport report, double? cellSize = null)
    {
        double w = doc.MaterialWidth, l = doc.MaterialLength(), t = doc.MaterialThickness;
        double cell = cellSize ?? CellSizeFor(w, l);
        int nx = Math.Max(1, (int)Math.Ceiling(w / cell)), ny = Math.Max(1, (int)Math.Ceiling(l / cell));
        var map = new StockHeightMap(nx, ny, cell, w, l, t);

        foreach (var m in report.Motion.Moves)
        {
            if (m.Kind != MoveKind.Feed || Math.Min(m.Z0, m.Z1) >= 0) continue; // only below the surface
            if (m.OperationIndex < 0 || m.OperationIndex >= report.Operations.Count) continue;
            var result = report.Operations[m.OperationIndex];
            var op = result.Operation;
            var pts = Points(m, result);
            double step = Math.Max(cell * 0.5, 0.05);
            for (int i = 1; i < pts.Count; i++)
            {
                var (ax, ay, az) = pts[i - 1]; var (bx, by, bz) = pts[i];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int n = Math.Max(1, (int)Math.Ceiling(len / step));
                for (int k = 0; k <= n; k++)
                {
                    double f = (double)k / n;
                    Stamp(map, ax + (bx - ax) * f, ay + (by - ay) * f, az + (bz - az) * f, op, l);
                }
            }
        }
        return map;
    }

    /// <summary>Material length helper (the design's Height property).</summary>
    private static double MaterialLength(this CncDocument d) => d.MaterialHeight;

    /// <summary>Polyline of a move in design coordinates with Z interpolated along it.</summary>
    private static List<(double X, double Y, double Z)> Points(MotionMove m, CamOperationResult r)
    {
        if (!m.FollowsPath || r.Preview is null)
            return new() { (m.X0, m.Y0, m.Z0), (m.X1, m.Y1, m.Z1) };
        var path = r.Preview.Centerlines.Count > 0 ? r.Preview.Centerlines[m.PathIndex] : r.Preview.Centerline;
        var slice = PathMath.Slice(path, m.S0, m.S1);
        double total = PathMath.Length(slice), run = 0;
        var list = new List<(double, double, double)>(slice.Count);
        for (int i = 0; i < slice.Count; i++)
        {
            if (i > 0) run += PathMath.Distance(slice[i - 1], slice[i]);
            double z = total > 0 ? m.Z0 + (m.Z1 - m.Z0) * run / total : m.Z1;
            list.Add((slice[i].X, slice[i].Y, z));
        }
        return list;
    }

    /// <summary>Lowers every cell under the tool at (x, y) with its tip at z. Design Y is flipped to work Y.</summary>
    private static void Stamp(StockHeightMap map, double x, double yDesign, double z, CamOperation op, double length)
    {
        double y = length - yDesign; // work Y: towards the back
        double r = op.ToolDiameter / 2;
        int ix0 = Math.Max(0, (int)Math.Floor((x - r) / map.Cell)), ix1 = Math.Min(map.Nx - 1, (int)Math.Floor((x + r) / map.Cell));
        int iy0 = Math.Max(0, (int)Math.Floor((y - r) / map.Cell)), iy1 = Math.Min(map.Ny - 1, (int)Math.Floor((y + r) / map.Cell));
        float floor = (float)-map.Thickness;
        double vTan = op.ToolKind == ToolKind.VBit ? Math.Tan(90.0 * Math.PI / 180 / 2) : 0; // V-bit angle not on the op yet: 90°
        for (int iy = iy0; iy <= iy1; iy++)
        {
            double cy = (iy + 0.5) * map.Cell - y;
            for (int ix = ix0; ix <= ix1; ix++)
            {
                double cx = (ix + 0.5) * map.Cell - x;
                double d2 = cx * cx + cy * cy;
                if (d2 > r * r) continue;
                double h = op.ToolKind switch
                {
                    ToolKind.BallNose => z + r - Math.Sqrt(r * r - d2),
                    ToolKind.VBit => z + Math.Sqrt(d2) / vTan,
                    _ => z
                };
                int idx = iy * map.Nx + ix;
                float v = (float)Math.Max(h, floor);
                if (v < map.Heights[idx]) map.Heights[idx] = v;
            }
        }
    }
}
