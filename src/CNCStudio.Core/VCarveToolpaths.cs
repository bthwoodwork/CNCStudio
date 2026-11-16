using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace CNCStudio.Core;

/// <summary>
/// V-carving: a V-bit carves the shape with walls that follow the outline
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
        if (op.Kind is not (CamOperationKind.VCarve or CamOperationKind.VCarvePlug)) throw new ArgumentException("Inte en V-carving.");
        if (CamValidation.GetError(op, materialThickness) is string problem) throw new ArgumentException(problem);
        if (!double.IsFinite(shape.X) || !double.IsFinite(shape.Y) || shape.Width <= 0 || shape.Height <= 0)
            throw new ArgumentException("Ogiltig objektgeometri.");
        if (shape.Kind == ShapeKind.VectorPath && (shape.Contours.Count == 0 || shape.Contours.Any(c => !c.Closed)))
            throw new ArgumentException("V-carving kräver slutna konturer.");
        double tan = Math.Tan(op.ToolAngleDeg * Math.PI / 360);
        double maxDepth = MaxDepth(op);
        bool plug = op.Kind == CamOperationKind.VCarvePlug;
        double start = plug ? op.InlayStartDepthMm : 0;
        if (start >= maxDepth - 1e-9)
            throw new ArgumentException($"Hanens startdjup ({start:0.##} mm) måste vara grundare än det djupaste V-fräsen når ({maxDepth:0.##} mm).");
        var region = plug ? PlugRegion(shape, op, maxDepth, tan) : PocketToolpaths.Region(shape);
        if (Math.Abs(Clipper.Area(region)) < 1e-6) throw new ArgumentException(plug ? "Hanen blev ingenting – minska spelet." : "Formen har ingen yta.");
        // Each loop at inset d is cut at start + d / tan: the cone meets the outline at depth 'start'.
        double maxInset = (maxDepth - start) * tan;
        // Inset step: fine enough that the stepped surface is smooth (≤ 0.1 mm in depth), never absurdly many loops.
        double step = Math.Clamp(Math.Min(0.1 * tan, maxInset / 8), 0.02, 0.25);

        var rings = new List<(PathsD Paths, double Depth)>();
        if (start > 0) rings.Add((Simplify(region), start));   // the first loop right on the outline, at the start depth
        bool floor = false;
        double d = step;
        for (; d < maxInset - 1e-9; d += step)
        {
            var inset = Simplify(Offset(region, -d));
            if (inset.Count == 0) break;
            rings.Add((inset, start + d / tan));
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
                // With a straight cutter clearing the bottom, the V-bit only cleans a band along its edge.
                double band = op.SecondToolEnabled ? CamSecondPass.VBandMm(op) : double.MaxValue, done = 0;
                for (var level = Simplify(Offset(atMax, -floorStep)); level.Count > 0 && (done += floorStep) <= band + 1e-9; level = Simplify(Offset(level, -floorStep)))
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

        // One letter (an outline with its holes) at a time, all its levels, before moving on to the next:
        // the tool stays in one place instead of jumping between letters for every loop.
        var components = Components(region);
        int CompOf(PathD p) { for (int c = 0; c < components.Count; c++) if (InComponent(p[0], components[c])) return c; return 0; }
        var byComp = components.Select(_ => new List<(List<PathD> Paths, double Depth)>()).ToList();
        foreach (var (paths, depth) in rings)
            foreach (var g in paths.Where(p => p.Count >= 3).GroupBy(CompOf))
                byComp[g.Key].Add((g.ToList(), depth));

        var loops = new List<IReadOnlyList<CamPoint>>();
        var depths = new List<double>();
        var cleared = new List<double>();
        var final = new List<bool>();
        var linkAtDepth = new List<bool>();
        CamPoint? at = null;
        int total = 0;
        var todo = Enumerable.Range(0, components.Count).Where(c => byComp[c].Count > 0).ToList();
        while (todo.Count > 0)
        {
            var here = at;
            int comp = here is null ? todo[0] : todo.OrderBy(c => byComp[c][0].Paths.Min(p => p.Min(v => Dist2(v, here)))).First();
            todo.Remove(comp);
            double previous = 0;
            foreach (double level in levels)
            {
                // Every loop still to cut at this level: (ring number, path).
                var items = new List<(int Ring, PathD Path, double Depth)>();
                for (int r = 0; r < byComp[comp].Count; r++)
                {
                    var (paths, depth) = byComp[comp][r];
                    if (depth <= previous + 1e-9) continue;
                    foreach (var p in paths) items.Add((r, p, depth));
                }
                var ringPoints = new Dictionary<int, IReadOnlyList<IReadOnlyList<CamPoint>>>();
                IReadOnlyList<IReadOnlyList<CamPoint>> RingPoints(int r) => ringPoints.TryGetValue(r, out var v) ? v
                    : ringPoints[r] = byComp[comp][r].Paths.Select(p => (IReadOnlyList<CamPoint>)p.Select(q => new CamPoint(q.x, q.y)).ToList()).ToList();
                int lastRing = -1;
                double lastDepth = 0;
                while (items.Count > 0)
                {
                    // Follow one part inwards as long as the next loop is right beside it (a chain: the outline's
                    // insets, or a hole's); otherwise start the outermost loop left, nearest first.
                    var near = at;
                    var next = items[0];
                    bool link = false;
                    if (near is not null && lastRing >= 0)
                    {
                        double best = double.MaxValue;
                        foreach (var it in items.Where(x => x.Ring == lastRing + 1))
                        {
                            double dd = it.Path.Min(v => Dist2(v, near));
                            if (dd < best) { best = dd; next = it; }
                        }
                        if (best <= 16 * step * step + 1e-9 && Math.Min(next.Depth, level) >= lastDepth - 1e-9)
                        {
                            var candidate = Rotate(next.Path, near);
                            link = PocketToolpaths.SegmentInside(near, candidate[0], RingPoints(lastRing).Where(p => p.Count >= 3).ToList());
                        }
                    }
                    if (!link)
                    {
                        int outer = items.Min(x => x.Ring);
                        var pool = items.Where(x => x.Ring == outer).ToList();
                        next = near is null ? pool[0] : pool.OrderBy(x => x.Path.Min(v => Dist2(v, near))).First();
                    }
                    items.Remove(next);
                    var loop = Rotate(next.Path, near);
                    total += loop.Count;
                    if (total > MaxPoints) throw new NotSupportedException("För komplex V-carving för förhandsvisning – minska djupet eller formens storlek.");
                    double cut = Math.Min(next.Depth, level);
                    loops.Add(loop);
                    depths.Add(cut);
                    cleared.Add(Math.Min(next.Depth, previous));
                    final.Add(next.Depth <= level + 1e-9);
                    linkAtDepth.Add(link);
                    at = loop[^1];
                    lastRing = next.Ring; lastDepth = cut;
                }
                previous = level;
            }
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
            LoopLinkAtDepth = linkAtDepth,
            VCarveMaxDepth = maxDepth,
            VCarveHasFloor = floor,
        };
    }

    /// <summary>
    /// Inlay plug: the band around the shape (made smaller by the clearance) that is carved away so the shape stands
    /// as a plug – wide enough that the middle of the band reaches the full depth.
    /// </summary>
    public static PathsD PlugRegion(DesignShape shape, CamOperation op, double maxDepth, double tan)
    {
        var plug = PocketToolpaths.Region(shape);
        if (op.InlayClearanceMm > 0) plug = Simplify(Offset(plug, -op.InlayClearanceMm));
        if (plug.Count == 0) return new PathsD();
        double band = PlugBandMm(op, maxDepth, tan);
        var outer = Offset(plug, band);
        return Clipper.Difference(outer, plug, FillRule.NonZero, Precision);
    }

    /// <summary>How far around the plug the V-bit carves.</summary>
    public static double PlugBandMm(CamOperation op, double maxDepth, double tan) =>
        2 * (maxDepth - op.InlayStartDepthMm) * tan + 2;

    /// <summary>The region's separate parts: each outer outline with its holes (islands in holes are parts of their own).</summary>
    private static List<PathsD> Components(PathsD region)
    {
        var tree = new PolyTreeD();
        Clipper.BooleanOp(ClipType.Union, region, null, tree, FillRule.EvenOdd, Precision);
        var result = new List<PathsD>();
        void Outer(PolyPathD outer)
        {
            var comp = new PathsD { outer.Polygon! };
            for (int i = 0; i < outer.Count; i++)
            {
                var hole = (PolyPathD)outer[i];
                comp.Add(hole.Polygon!);
                for (int k = 0; k < hole.Count; k++) Outer((PolyPathD)hole[k]);
            }
            result.Add(comp);
        }
        for (int i = 0; i < tree.Count; i++) Outer((PolyPathD)tree[i]);
        if (result.Count == 0) result.Add(region);
        return result;
    }

    private static bool InComponent(PointD p, PathsD comp) =>
        Clipper.PointInPolygon(p, comp[0]) != PointInPolygonResult.IsOutside &&
        comp.Skip(1).All(h => Clipper.PointInPolygon(p, h) != PointInPolygonResult.IsInside);

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
