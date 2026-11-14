using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace CNCStudio.Core;

/// <summary>
/// V-carving (as in Easel Pro, VCarve and UGS): a V-bit carves the shape with walls that follow the outline
/// exactly – sharp corners stay sharp and narrow parts get shallow, wide parts deep.
/// <para>
/// Method (offset V-carving): the region is inset in small steps d; the loop at inset d is cut at depth
/// d / tan(angle/2), so the cone touches the outline exactly at the surface. Together the loops give the
/// carved surface the distance from the outline decides (the medial axis is the deepest line). The depth is
/// limited by the total depth and by the bit's width (the cone is no wider than the bit). Where the shape is
/// wider than that, the floor is cleared at the limit depth with loops close enough to leave ridges of at most
/// <see cref="FloorRidgeMm"/> – a flat-bottomed V-carve.
/// </para>
/// <para>Passes: with depth per pass less than the deepest loop, the loops are cut level by level.</para>
/// </summary>
public static class VCarveToolpaths
{
    /// <summary>Highest ridge left between floor loops (mm).</summary>
    public const double FloorRidgeMm = 0.2;
    private const int Precision = 4;
    private const double ArcTolerance = 0.01;
    private const double SimplifyEpsilon = 0.005;
    private const int MaxPoints = 600_000;
    private const int MaxLoops = 20_000;

    public static string? ValidateAngle(double angleDeg) =>
        !double.IsFinite(angleDeg) || angleDeg < 10 || angleDeg > 170 ? "V-fräsens vinkel måste vara 10–170 grader." : null;

    /// <summary>Half of the cone's width at a depth: depth · tan(angle/2).</summary>
    public static double HalfWidthAt(double depth, double angleDeg) => depth * Math.Tan(angleDeg * Math.PI / 360);

    /// <summary>The deepest the bit can cut: its full width reached, or the total depth.</summary>
    public static double MaxDepth(CamOperation op) =>
        Math.Min(op.TotalDepth, op.ToolDiameter / 2 / Math.Tan(op.ToolAngleDeg * Math.PI / 360));

    public static ContourPreview Generate(DesignShape shape, CamOperation op, double materialThickness)
    {
        if (op.Kind != CamOperationKind.VCarve) throw new ArgumentException("Inte en V-carving.");
        if (CamValidation.GetError(op, materialThickness) is string problem) throw new ArgumentException(problem);
        if (!double.IsFinite(shape.X) || !double.IsFinite(shape.Y) || shape.Width <= 0 || shape.Height <= 0)
            throw new ArgumentException("Ogiltig objektgeometri.");
        if (shape.Kind == ShapeKind.VectorPath && (shape.Contours.Count == 0 || shape.Contours.Any(c => !c.Closed)))
            throw new ArgumentException("V-carving kräver slutna konturer.");
        var region = PocketToolpaths.Region(shape);
        if (Math.Abs(Clipper.Area(region)) < 1e-6) throw new ArgumentException("Formen har ingen yta.");

        double tan = Math.Tan(op.ToolAngleDeg * Math.PI / 360);
        double maxDepth = MaxDepth(op);
        double maxInset = maxDepth * tan;
        // Inset step: fine enough that the stepped surface is smooth (≤ 0.1 mm in depth), never absurdly many loops.
        double step = Math.Clamp(Math.Min(0.1 * tan, maxInset / 8), 0.02, 0.25);

        var rings = new List<(PathsD Paths, double Depth)>();
        bool floor = false;
        double d = step;
        for (; d < maxInset - 1e-9; d += step)
        {
            var inset = Simplify(Offset(region, -d));
            if (inset.Count == 0) break;
            rings.Add((inset, d / tan));
            if (rings.Count > MaxLoops) throw new NotSupportedException("För många varv – minska djupet eller välj en V-fräs med större vinkel.");
        }
        if (d >= maxInset - 1e-9)
        {
            // Last full-depth loop, then the floor: loops at the limit depth spaced so the ridges stay low.
            var atMax = Simplify(Offset(region, -maxInset));
            if (atMax.Count > 0)
            {
                rings.Add((atMax, maxDepth));
                double floorStep = Math.Max(2 * FloorRidgeMm * tan, 0.05);
                for (var level = Simplify(Offset(atMax, -floorStep)); level.Count > 0; level = Simplify(Offset(level, -floorStep)))
                {
                    floor = true;
                    rings.Add((level, maxDepth));
                    if (rings.Count > MaxLoops) throw new NotSupportedException("För stor yta att rensa med V-fräsen – fickfräs botten med en rak fräs först.");
                }
            }
        }
        if (rings.Count == 0) throw new ArgumentException("Formen är för smal för V-fräsen – inget kan fräsas.");

        // Depth levels: every loop is cut at most DepthPerPass deeper than the time before.
        double deepest = rings.Max(r => r.Depth);
        var levels = new List<double>();
        for (double z = op.DepthPerPass; z < deepest - 1e-9; z += op.DepthPerPass) levels.Add(z);
        levels.Add(deepest);

        var loops = new List<IReadOnlyList<CamPoint>>();
        var depths = new List<double>();
        var cleared = new List<double>();
        var final = new List<bool>();
        CamPoint? at = null;
        int total = 0;
        double previous = 0;
        foreach (double level in levels)
        {
            // Outside in within a level (shallow first): each loop cut where it is not finished yet.
            foreach (var (paths, depth) in rings)
            {
                if (depth <= previous + 1e-9) continue;
                var remaining = paths.Where(p => p.Count >= 3).ToList();
                while (remaining.Count > 0)
                {
                    var here = at;
                    var next = here is null ? remaining[0] : remaining.OrderBy(p => p.Min(v => Dist2(v, here))).First();
                    remaining.Remove(next);
                    var loop = Rotate(next, here);
                    total += loop.Count;
                    if (total > MaxPoints) throw new NotSupportedException("För komplex V-carving för förhandsvisning – minska djupet eller formens storlek.");
                    loops.Add(loop);
                    depths.Add(Math.Min(depth, level));
                    cleared.Add(Math.Min(depth, previous));
                    final.Add(depth <= level + 1e-9);
                    at = loop[^1];
                }
            }
            previous = level;
        }

        string warning = floor
            ? $"Bredare partier än {2 * maxInset:0.##} mm rensas plant på {maxDepth:0.##} mm djup med V-fräsen (åsar högst {FloorRidgeMm:0.##} mm)."
            : "";
        return new ContourPreview(loops[0], levels, warning)
        {
            Centerlines = loops,
            IsVCarve = true,
            LoopDepths = depths,
            LoopClearedDepths = cleared,
            LoopFinal = final,
            VCarveMaxDepth = maxDepth,
            VCarveHasFloor = floor,
        };
    }

    private static PathsD Simplify(PathsD paths) =>
        new(Clipper.SimplifyPaths(paths, SimplifyEpsilon, true).Where(p => p.Count >= 3));

    private static PathsD Offset(PathsD paths, double delta) =>
        Clipper.InflatePaths(paths, delta, JoinType.Round, EndType.Polygon, 2.0, Precision, ArcTolerance);

    private static double Dist2(PointD v, CamPoint p) => (v.x - p.X) * (v.x - p.X) + (v.y - p.Y) * (v.y - p.Y);

    /// <summary>Closed loop starting at the vertex nearest <paramref name="near"/>; last point = first.</summary>
    private static List<CamPoint> Rotate(PathD path, CamPoint? near)
    {
        int start = 0;
        if (near is not null)
        {
            double best = double.MaxValue;
            for (int i = 0; i < path.Count; i++) { double d = Dist2(path[i], near); if (d < best) { best = d; start = i; } }
        }
        var loop = new List<CamPoint>(path.Count + 1);
        for (int i = 0; i <= path.Count; i++) { var v = path[(start + i) % path.Count]; loop.Add(new CamPoint(v.x, v.y)); }
        return loop;
    }
}
