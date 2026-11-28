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

    /// <summary>An independent copy (the same cells, its own heights).</summary>
    public StockHeightMap Clone()
    {
        var copy = new StockHeightMap(Nx, Ny, Cell, Width, Length, Thickness);
        Array.Copy(Heights, copy.Heights, Heights.Length);
        return copy;
    }
}

/// <summary>The cutting tool's shape for the material simulation.</summary>
public readonly record struct ToolSpec(ToolKind Kind, double Diameter, double AngleDeg)
{
    public static ToolSpec Of(CamOperation op) => new(op.ToolKind, op.ToolDiameter, op.ToolAngleDeg);
}

/// <summary>
/// The material while a program runs: the program's cutting moves (parsed G-code, work coordinates) are cut into the
/// map up to where the machine is – move by move, part of the current one – so the 3D view shows what has been cut so
/// far. <see cref="Advance"/> only cuts what is new since the last call; going back (a new run) starts over from the
/// material the program started with (for program 2 of a multi-tool job: what program 1 left).
/// A visual estimate from the planned path, like <see cref="StockSimulator"/>.
/// </summary>
public sealed class LiveStockSimulator
{
    private readonly StockHeightMap _start;
    private readonly ToolSpec _tool;
    private readonly double _offsetX, _offsetY;

    /// <param name="start">The material before the program (copied); its cells are used.</param>
    /// <param name="workZero">Where G54 X0 Y0 is on the material: work coordinates are moved to the map's front-left origin.</param>
    public LiveStockSimulator(StockHeightMap start, WorkZero workZero, ToolSpec tool)
    {
        _start = start.Clone();
        Map = start.Clone();
        _tool = tool;
        if (workZero == WorkZero.Center) { _offsetX = start.Width / 2; _offsetY = start.Length / 2; }
    }

    public StockHeightMap Map { get; private set; }
    /// <summary>Moves before this index are cut; the one at it up to <see cref="Fraction"/>.</summary>
    public int Index { get; private set; }
    public double Fraction { get; private set; }

    /// <summary>
    /// Cuts up to move <paramref name="index"/>, part <paramref name="fraction"/> (0–1) of it. True when the material
    /// changed. Rapids never cut; nor does anything above the surface.
    /// </summary>
    public bool Advance(IReadOnlyList<CNCStudio.Core.GCode.ProgramMove> moves, int index, double fraction)
    {
        index = Math.Clamp(index, 0, moves.Count);
        fraction = index >= moves.Count ? 0 : Math.Clamp(fraction, 0, 1);
        bool changed = false;
        if (index < Index || index == Index && fraction < Fraction - 1e-9)
        {
            Map = _start.Clone(); Index = 0; Fraction = 0; changed = true;   // back to the start (a new run)
        }
        for (int i = Index; i <= index && i < moves.Count; i++)
        {
            double from = i == Index ? Fraction : 0, to = i == index ? fraction : 1;
            if (to <= from + 1e-12) continue;
            var m = moves[i];
            if (m.Rapid || Math.Min(m.Z0, m.Z1) >= 0) continue;
            double dx = m.X1 - m.X0, dy = m.Y1 - m.Y0, dz = m.Z1 - m.Z0;
            StockSimulator.StampSegment(Map,
                m.X0 + dx * from + _offsetX, m.Y0 + dy * from + _offsetY, m.Z0 + dz * from,
                m.X0 + dx * to + _offsetX, m.Y0 + dy * to + _offsetY, m.Z0 + dz * to, _tool);
            changed = true;
        }
        Index = index; Fraction = fraction;
        return changed;
    }
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
        var map = NewMap(doc, cellSize);
        Apply(map, doc, report.Operations, report.Motion.Moves);
        return map;
    }

    /// <summary>Uncut material for the document, in cells of <paramref name="cellSize"/> (default: <see cref="CellSizeFor"/>).</summary>
    public static StockHeightMap NewMap(CncDocument doc, double? cellSize = null)
    {
        double w = doc.MaterialWidth, l = doc.MaterialLength(), t = doc.MaterialThickness;
        double cell = cellSize ?? CellSizeFor(w, l);
        int nx = Math.Max(1, (int)Math.Ceiling(w / cell)), ny = Math.Max(1, (int)Math.Ceiling(l / cell));
        return new StockHeightMap(nx, ny, cell, w, l, t);
    }

    /// <summary>Cuts the planned moves of these operations into the map (e.g. one program of a multi-tool job).</summary>
    public static void Apply(StockHeightMap map, CncDocument doc, IReadOnlyList<CamOperationResult> operations, IEnumerable<MotionMove> moves)
    {
        double l = doc.MaterialLength();
        foreach (var m in moves)
        {
            if (m.Kind != MoveKind.Feed || Math.Min(m.Z0, m.Z1) >= 0) continue; // only below the surface
            if (m.OperationIndex < 0 || m.OperationIndex >= operations.Count) continue;
            var result = operations[m.OperationIndex];
            var tool = ToolSpec.Of(result.Operation);
            var pts = Points(m, result);
            for (int i = 1; i < pts.Count; i++)
            {
                var (ax, ay, az) = pts[i - 1]; var (bx, by, bz) = pts[i];
                StampSegment(map, ax, l - ay, az, bx, l - by, bz, tool);   // design Y is flipped to work Y
            }
        }
    }

    /// <summary>
    /// Stamps the tool along a straight line from a to b (stock coordinates: X from the left edge, Y from the front
    /// edge, Z 0 = top), closely enough that no cell between the stamps is missed.
    /// </summary>
    public static void StampSegment(StockHeightMap map, double ax, double ay, double az, double bx, double by, double bz, ToolSpec tool)
    {
        if (Math.Min(az, bz) >= 0) return;
        double step = Math.Max(map.Cell * 0.5, 0.05);
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        int n = Math.Max(1, (int)Math.Ceiling(len / step));
        for (int k = 0; k <= n; k++)
        {
            double f = (double)k / n;
            Stamp(map, ax + (bx - ax) * f, ay + (by - ay) * f, az + (bz - az) * f, tool);
        }
    }

    /// <summary>Material length helper (the design's Height property).</summary>
    private static double MaterialLength(this CncDocument d) => d.MaterialHeight;

    /// <summary>Polyline of a move in design coordinates with Z interpolated along it.</summary>
    private static List<(double X, double Y, double Z)> Points(MotionMove m, CamOperationResult r)
    {
        if (!m.FollowsPath || r.Preview is null)
            return new() { (m.X0, m.Y0, m.Z0), (m.X1, m.Y1, m.Z1) };
        if (r.Preview.Is3D)
            return r.Preview.Centerlines[m.PathIndex].Select((p, i) => (p.X, p.Y, r.Preview.PathZ[m.PathIndex][i])).ToList();
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

    /// <summary>Lowers every cell under the tool at (x, y) (stock coordinates, Y from the front) with its tip at z.</summary>
    private static void Stamp(StockHeightMap map, double x, double y, double z, ToolSpec op)
    {
        double tipR = op.Diameter / 2;
        // A tapered ball nose widens upwards: it reaches as far out as its cone is wide at the material's top.
        double r = op.Kind == ToolKind.TaperedBallNose ? ToolShape.RadiusAt(op.Kind, tipR, op.AngleDeg, Math.Max(0, -z)) : tipR;
        int ix0 = Math.Max(0, (int)Math.Floor((x - r) / map.Cell)), ix1 = Math.Min(map.Nx - 1, (int)Math.Floor((x + r) / map.Cell));
        int iy0 = Math.Max(0, (int)Math.Floor((y - r) / map.Cell)), iy1 = Math.Min(map.Ny - 1, (int)Math.Floor((y + r) / map.Cell));
        float floor = (float)-map.Thickness;
        double vTan = op.Kind == ToolKind.VBit ? Math.Tan(op.AngleDeg * Math.PI / 360) : 0;
        for (int iy = iy0; iy <= iy1; iy++)
        {
            double cy = (iy + 0.5) * map.Cell - y;
            for (int ix = ix0; ix <= ix1; ix++)
            {
                double cx = (ix + 0.5) * map.Cell - x;
                double d2 = cx * cx + cy * cy;
                if (d2 > r * r) continue;
                double h = op.Kind switch
                {
                    ToolKind.BallNose => z + r - Math.Sqrt(r * r - d2),
                    ToolKind.VBit => z + Math.Sqrt(d2) / vTan,
                    ToolKind.TaperedBallNose => z + ToolShape.Height(op.Kind, tipR, op.AngleDeg, Math.Sqrt(d2)),
                    _ => z
                };
                int idx = iy * map.Nx + ix;
                float v = (float)Math.Max(h, floor);
                if (v < map.Heights[idx]) map.Heights[idx] = v;
            }
        }
    }
}
