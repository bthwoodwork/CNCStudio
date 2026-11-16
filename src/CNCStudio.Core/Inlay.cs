using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Clipper2Lib;

namespace CNCStudio.Core;

/// <summary>
/// A V-carve inlay: the pocket (hona) is V-carved into the base board, the plug
/// (hane) – mirrored – is carved standing on another board, turned over and glued into the pocket.
/// <para>
/// Why it fits: the pocket's walls are the V-bit's cone from the outline at the surface. The plug's walls are the
/// same cone from the outline at the start depth S. Turned over, the plug's top goes S down into the pocket before
/// the walls meet – so the pocket must be deeper than S: the gap left at the bottom (pocket depth − S) holds the
/// glue. The plug stands (plug depth − S) above the board; that is sawn or planed off once the glue has set.
/// </para>
/// </summary>
public sealed class InlayPlan
{
    public double AngleDeg { get; set; } = 60;
    public double ToolDiameter { get; set; } = 12.7;
    public string? ToolName { get; set; }
    /// <summary>The pocket's depth in the base board (flat bottom where the design is wide).</summary>
    public double PocketDepthMm { get; set; } = 4;
    /// <summary>The plug's carving starts this deep at the outline.</summary>
    public double PlugStartDepthMm { get; set; } = 3;
    /// <summary>How deep the plug is carved around (its height above the plug board's floor).</summary>
    public double PlugDepthMm { get; set; } = 5;
    /// <summary>The plug is made this much smaller all round (glue on the walls).</summary>
    public double ClearanceMm { get; set; } = 0.05;
    public double PlugBoardThicknessMm { get; set; } = 18;
    public double FeedRate { get; set; } = 1200;
    public double PlungeRate { get; set; } = 300;
    public double SpindleRpm { get; set; } = 18000;
    public double DepthPerPass { get; set; } = 3;

    public double Tan => Math.Tan(AngleDeg * Math.PI / 360);
    /// <summary>The deepest the bit cuts at its full width.</summary>
    public double ToolMaxDepth => ToolDiameter / 2 / Tan;
    /// <summary>Glue gap at the bottom of the pocket when the plug is in.</summary>
    public double BottomGapMm => PocketDepthMm - PlugStartDepthMm;
    /// <summary>How far the plug stands above the base board's surface (sawn/planed off afterwards).</summary>
    public double ProtrusionMm => PlugDepthMm - PlugStartDepthMm;

    public string? Validate(double baseThicknessMm)
    {
        if (VCarveToolpaths.ValidateAngle(AngleDeg) is string a) return a;
        if (!Ok(ToolDiameter, 0.5, 100)) return "Ange V-fräsens diameter.";
        if (!Ok(PocketDepthMm, 0.2, baseThicknessMm)) return "Honans djup måste vara större än 0 och mindre än materialets tjocklek.";
        if (!Ok(PlugStartDepthMm, 0, 50) || PlugStartDepthMm >= PocketDepthMm)
            return "Hanens startdjup måste vara mindre än honans djup – annars blir det ingen glipa för lim i botten.";
        if (!Ok(PlugDepthMm, 0.2, PlugBoardThicknessMm) || PlugDepthMm <= PlugStartDepthMm)
            return "Hanens djup måste vara större än startdjupet och mindre än hanens skiva.";
        if (!Ok(ClearanceMm, 0, 1)) return "Spelet måste vara 0–1 mm.";
        if (!Ok(PlugBoardThicknessMm, 1, 200)) return "Ange tjockleken på skivan till hanen.";
        if (!Ok(FeedRate, 1, 20000) || !Ok(PlungeRate, 1, 20000)) return "Ogiltig matning.";
        if (!Ok(SpindleRpm, 0, 60000)) return "Ogiltigt varvtal.";
        if (!Ok(DepthPerPass, 0.1, 50)) return "Ogiltigt djup per pass.";
        if (ToolMaxDepth < PocketDepthMm - 1e-9)
            return $"V-fräsen (Ø {ToolDiameter:0.##} mm, {AngleDeg:0.#}°) är full bredd redan på {ToolMaxDepth:0.##} mm – honan kan inte bli {PocketDepthMm:0.##} mm djup. Välj grundare honan eller en bredare fräs.";
        return null;
    }

    private static bool Ok(double v, double min, double max) => double.IsFinite(v) && v >= min && v <= max;
}

public static class Inlay
{
    /// <summary>Free board around the plug's carved band (mm).</summary>
    public const double PlugBoardMarginMm = 10;

    public static CamOperation PocketOperation(DesignShape shape, InlayPlan p) => new()
    {
        ShapeId = shape.Id, Kind = CamOperationKind.VCarve, ToolKind = ToolKind.VBit, ToolName = p.ToolName,
        ToolDiameter = p.ToolDiameter, ToolAngleDeg = p.AngleDeg, TotalDepth = p.PocketDepthMm, DepthPerPass = p.DepthPerPass,
        UseTabs = false, FeedRate = p.FeedRate, PlungeRate = p.PlungeRate, SpindleRpm = p.SpindleRpm,
    };

    public static CamOperation PlugOperation(Guid shapeId, InlayPlan p) => new()
    {
        ShapeId = shapeId, Kind = CamOperationKind.VCarvePlug, ToolKind = ToolKind.VBit, ToolName = p.ToolName,
        ToolDiameter = p.ToolDiameter, ToolAngleDeg = p.AngleDeg, TotalDepth = Math.Min(p.PlugDepthMm, p.ToolMaxDepth), DepthPerPass = p.DepthPerPass,
        InlayStartDepthMm = p.PlugStartDepthMm, InlayClearanceMm = p.ClearanceMm,
        UseTabs = false, FeedRate = p.FeedRate, PlungeRate = p.PlungeRate, SpindleRpm = p.SpindleRpm,
    };

    /// <summary>
    /// The shapes as one region, mirrored left–right (the plug is turned over to go in), as a single vector shape
    /// with its top-left corner at (x, y): one shape, so the carving around one letter never cuts into the next.
    /// </summary>
    public static DesignShape MirroredUnion(IReadOnlyList<DesignShape> shapes, double x, double y)
    {
        if (shapes.Count == 0) throw new ArgumentException("Markera formen som ska läggas in.");
        var all = new PathsD();
        foreach (var s in shapes)
            all.AddRange(Clipper.Union(PocketToolpaths.Region(s), FillRule.EvenOdd));
        var region = Clipper.Union(all, FillRule.NonZero);
        if (region.Count == 0) throw new ArgumentException("Formen har ingen yta.");
        double x0 = region.Min(p => p.Min(v => v.x)), x1 = region.Max(p => p.Max(v => v.x));
        double y0 = region.Min(p => p.Min(v => v.y)), y1 = region.Max(p => p.Max(v => v.y));
        double w = x1 - x0, h = y1 - y0;
        return new DesignShape
        {
            Kind = ShapeKind.VectorPath, X = x, Y = y, Width = w, Height = h,
            Contours = region.Select(p => new VectorContour
            {
                Closed = true,
                Points = p.Select(v => new VectorPoint((x1 - v.x) / w, (v.y - y0) / h)).ToList()   // mirrored in X
            }).ToList(),
        };
    }

    /// <summary>
    /// The plug project: the same machine and settings, a board just big enough for the mirrored plug and the band
    /// carved around it, and the plug operation.
    /// </summary>
    public static CncDocument PlugDocument(CncDocument baseDoc, IReadOnlyList<DesignShape> shapes, InlayPlan p)
    {
        var json = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var doc = JsonSerializer.Deserialize<CncDocument>(JsonSerializer.Serialize(baseDoc, json), json)!;
        var probe = PlugOperation(Guid.Empty, p);
        double band = VCarveToolpaths.PlugBandMm(probe, VCarveToolpaths.MaxDepth(probe), p.Tan);
        double margin = band + PlugBoardMarginMm;
        var plug = MirroredUnion(shapes, margin, margin);
        doc.Name = (string.IsNullOrWhiteSpace(baseDoc.Name) ? "Inlay" : baseDoc.Name) + " – hane";
        doc.Shapes = new List<DesignShape> { plug };
        doc.CamOperations = new List<CamOperation> { PlugOperation(plug.Id, p) };
        doc.Fixtures = new List<FixtureBlock>();          // another board: clamp it and draw its fixtures again
        doc.Inlay = null;
        doc.Motion.StockTopMachineZ = null;               // another board: its surface is probed/zeroed again
        doc.HasMaterial = true;
        doc.MaterialWidth = Math.Ceiling(plug.Width + 2 * margin);
        doc.MaterialHeight = Math.Ceiling(plug.Height + 2 * margin);
        doc.MaterialThickness = p.PlugBoardThicknessMm;
        // The same corner on the table, moved in if the new board would stick out.
        var m = doc.Machine ?? new MachineProfile();
        doc.Stock.X = Math.Max(0, Math.Min(doc.Stock.X, m.WorkWidthMm - doc.MaterialWidth));
        doc.Stock.Y = Math.Max(0, Math.Min(doc.Stock.Y, m.WorkLengthMm - doc.MaterialHeight));
        return doc;
    }
}
