using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace CNCStudio.Core;

/// <summary>
/// Grov- och finbearbetning med två fräsar.
/// An operation with a second tool becomes two passes – each its own program, as every tool is:
/// <list type="bullet">
/// <item>Pocket: the main (big) cutter clears the pocket; the second (smaller) cutter then cuts only the rest –
/// the corners and narrow parts the big one could not reach (rest machining).</item>
/// <item>V-carving: the second (straight) cutter first clears the wide flat bottoms at full depth; the V-bit then only
/// carves the sloping edges and a narrow band of the bottom along them – much faster than clearing with the V-bit.</item>
/// </list>
/// </summary>
public static class CamSecondPass
{
    private const int Precision = 4;
    private static readonly double DefaultAngleDeg = new CamOperation().ToolAngleDeg;

    /// <summary>The operations as they run: each with a second tool expanded into its two passes.</summary>
    public static IEnumerable<CamOperation> Expand(IEnumerable<CamOperation> ops)
    {
        foreach (var op in ops)
        {
            var second = For(op);
            if (ChamferFor(op) is { } chamfer) yield return chamfer;                                  // the edge while the part is held
            if (second is not null && second.Kind == CamOperationKind.VCarveClear) yield return second;   // flat bottoms first
            yield return op;
            if (second is not null && second.Kind is CamOperationKind.PocketRest or CamOperationKind.Carve3DFinish) yield return second;   // details / finishing after
        }
    }

    /// <summary>The chamfer pass (V-bit along the top edge) of a contour or pocket, or null without one.</summary>
    public static CamOperation? ChamferFor(CamOperation op)
    {
        if (!op.ChamferEnabled || op.Parent is not null || op.Kind is not (CamOperationKind.Outside or CamOperationKind.Inside or CamOperationKind.Pocket)) return null;
        double depth = ChamferToolpaths.Depth(op.ChamferWidthMm, op.ChamferAngleDeg);
        return new CamOperation
        {
            ShapeId = op.ShapeId, Kind = CamOperationKind.Chamfer, Parent = op,
            ToolName = op.ChamferToolName, ToolKind = ToolKind.VBit, ToolDiameter = op.ChamferToolDiameter, ToolAngleDeg = op.ChamferAngleDeg,
            ChamferWidthMm = op.ChamferWidthMm, TotalDepth = Math.Round(depth, 4), DepthPerPass = Math.Round(depth, 4), UseTabs = false,
            FeedRate = op.ChamferFeedRate, PlungeRate = op.ChamferPlungeRate, SpindleRpm = op.ChamferSpindleRpm, Direction = op.Direction,
        };
    }

    /// <summary>The second tool's pass of <paramref name="op"/>, or null without one.</summary>
    public static CamOperation? For(CamOperation op)
    {
        if (!op.SecondToolEnabled || op.Parent is not null) return null;
        CamOperationKind kind;
        double depth;
        switch (op.Kind)
        {
            case CamOperationKind.Pocket: kind = CamOperationKind.PocketRest; depth = op.TotalDepth; break;
            case CamOperationKind.Carve3D: kind = CamOperationKind.Carve3DFinish; depth = op.TotalDepth; break;
            case CamOperationKind.VCarve or CamOperationKind.VCarvePlug:
                kind = CamOperationKind.VCarveClear;
                depth = VCarveToolpaths.MaxDepth(op);
                break;
            default: return null;
        }
        return new CamOperation
        {
            ShapeId = op.ShapeId, Kind = kind, Parent = op,
            ToolName = op.SecondToolName, ToolKind = kind == CamOperationKind.Carve3DFinish ? op.SecondToolKind : ToolKind.FlatEndMill, ToolDiameter = op.SecondToolDiameter,
            ToolAngleDeg = kind == CamOperationKind.Carve3DFinish ? op.SecondToolAngleDeg : DefaultAngleDeg,
            TotalDepth = depth, DepthPerPass = op.SecondDepthPerPass, UseTabs = false,
            FeedRate = op.SecondFeedRate, PlungeRate = op.SecondPlungeRate, SpindleRpm = op.SecondSpindleRpm,
            StepoverPercent = op.Kind == CamOperationKind.Pocket ? op.StepoverPercent : op.Kind == CamOperationKind.Carve3D ? 10 : 40,
            Plunge = op.Plunge, RampAngleDeg = op.RampAngleDeg, RampFeedPercent = op.RampFeedPercent, Direction = op.Direction,
        };
    }

    /// <summary>The second pass's toolpath: a pocket, but only where it is needed.</summary>
    public static ContourPreview Generate(DesignShape shape, CamOperation op, double materialThickness)
    {
        var parent = op.Parent ?? throw new ArgumentException("Andra fräsens pass saknar sin operation.");
        if (CamValidation.GetError(op, materialThickness) is string problem) throw new ArgumentException(problem);
        PathsD area = op.Kind == CamOperationKind.PocketRest ? RestArea(shape, parent, op) : FloorArea(shape, parent);
        var pocketShape = ShapeOf(area) ?? throw new ArgumentException(op.Kind == CamOperationKind.PocketRest
            ? "Den stora fräsen når hela fickan – detaljfräsen behövs inte här."
            : "Formen har inga breda bottnar – den raka fräsen behövs inte här (V-fräsen gör allt).");
        var pocket = new CamOperation
        {
            ShapeId = op.ShapeId, Kind = CamOperationKind.Pocket, ToolDiameter = op.ToolDiameter, TotalDepth = op.TotalDepth,
            DepthPerPass = op.DepthPerPass, UseTabs = false, StepoverPercent = op.StepoverPercent, FeedRate = op.FeedRate, PlungeRate = op.PlungeRate,
            SpindleRpm = op.SpindleRpm, Plunge = op.Plunge, RampAngleDeg = op.RampAngleDeg, RampFeedPercent = op.RampFeedPercent,
        };
        try { return PocketToolpaths.Generate(pocketShape, pocket, materialThickness); }
        catch (ArgumentException ex) when (ex.Message.Contains("ryms inte"))
        {
            throw new ArgumentException(op.Kind == CamOperationKind.PocketRest
                ? $"Detaljfräsen (Ø {op.ToolDiameter:0.###} mm) ryms inte heller i det som är kvar – välj en ännu mindre."
                : $"Den raka fräsen (Ø {op.ToolDiameter:0.###} mm) ryms inte i bottnarna – välj en mindre.");
        }
    }

    /// <summary>
    /// What the big cutter left (the pocket minus everything its edge reached), grown by the small cutter's radius
    /// into the cleared part so the small cutter's loops cover all of it.
    /// </summary>
    public static PathsD RestArea(DesignShape shape, CamOperation big, CamOperation small)
    {
        var region = PocketToolpaths.Region(shape);
        double R = big.ToolDiameter / 2, r = small.ToolDiameter / 2;
        var centres = Inflate(region, -R);
        var reached = centres.Count == 0 ? new PathsD() : Inflate(centres, R);
        var rest = Clipper.Difference(region, reached, FillRule.NonZero, Precision);
        // What the small cutter cannot reach either (corners tighter than r) is not worth a pass of its own.
        var smallCentres = Inflate(region, -r);
        var smallReach = smallCentres.Count == 0 ? new PathsD() : Inflate(smallCentres, r);
        var useful = Clipper.Intersect(rest, smallReach, FillRule.NonZero, Precision);
        useful = new PathsD(useful.Where(p => Math.Abs(Clipper.Area(p)) > 0.05 * r * r));
        if (Math.Abs(Clipper.Area(useful)) < Math.Max(0.2, 0.1 * r * r)) return new PathsD();
        return Clipper.Intersect(Inflate(useful, r + 0.2), region, FillRule.NonZero, Precision);
    }

    /// <summary>
    /// V-carving's flat bottom (where the carving is at full depth), less a band along its edge that the V-bit
    /// still cuts – so the straight cutter's wall never touches the V's slope.
    /// </summary>
    public static PathsD FloorArea(DesignShape shape, CamOperation v)
    {
        double tan = Math.Tan(v.ToolAngleDeg * Math.PI / 360);
        double maxDepth = VCarveToolpaths.MaxDepth(v);
        double start = v.Kind == CamOperationKind.VCarvePlug ? v.InlayStartDepthMm : 0;
        var region = v.Kind == CamOperationKind.VCarvePlug ? VCarveToolpaths.PlugRegion(shape, v, maxDepth, tan) : PocketToolpaths.Region(shape);
        return Inflate(region, -(maxDepth - start) * tan);
    }

    /// <summary>How far into the bottom (beyond the full-depth line) the V-bit still cuts when a straight cutter clears the rest.</summary>
    public static double VBandMm(CamOperation v) => v.SecondToolDiameter / 2 + 0.5;

    private static PathsD Inflate(PathsD p, double d) =>
        new(Clipper.SimplifyPaths(Clipper.InflatePaths(p, d, JoinType.Round, EndType.Polygon, 2.0, Precision, 0.01), 0.005, true).Where(q => q.Count >= 3));

    private static DesignShape? ShapeOf(PathsD area)
    {
        if (area.Count == 0 || Math.Abs(Clipper.Area(area)) < 1e-3) return null;
        double x0 = area.Min(p => p.Min(v => v.x)), x1 = area.Max(p => p.Max(v => v.x));
        double y0 = area.Min(p => p.Min(v => v.y)), y1 = area.Max(p => p.Max(v => v.y));
        double w = Math.Max(x1 - x0, 1e-6), h = Math.Max(y1 - y0, 1e-6);
        return new DesignShape
        {
            Kind = ShapeKind.VectorPath, X = x0, Y = y0, Width = w, Height = h,
            Contours = area.Select(p => new VectorContour { Closed = true, Points = p.Select(v => new VectorPoint((v.x - x0) / w, (v.y - y0) / h)).ToList() }).ToList(),
        };
    }
}
