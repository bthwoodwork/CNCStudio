using System;

namespace CNCStudio.Core;

/// <summary>What the wasteboard surfacing tool asks for (like gSender's Wasteboard Surfacing).</summary>
public sealed record SurfacingSettings
{
    /// <summary>The area to surface, X × Y in mm. Null = as large as the machine allows (see <see cref="WasteboardSurfacing.MaxArea"/>).</summary>
    public double? Width { get; init; }
    public double? Length { get; init; }
    /// <summary>The area's nearest corner, measured from the home corner (as Materialplacering shows it). Null = centred on the table.</summary>
    public double? FromHomeX { get; init; }
    public double? FromHomeY { get; init; }
    public double WasteboardThickness { get; init; } = 18;
    public double TotalDepth { get; init; } = 0.5;
    public double DepthPerPass { get; init; } = 0.5;
    public string ToolName { get; init; } = "Planfräs";
    public double ToolDiameter { get; init; } = 25;
    public double StepoverPercent { get; init; } = 60;
    public double FeedRate { get; init; } = 2000;
    public double PlungeRate { get; init; } = 300;
    public double SpindleRpm { get; init; } = 18000;
    /// <summary>Rows along Y instead of along X.</summary>
    public bool RowsAlongY { get; init; }
    /// <summary>How far past the edge each row runs, beyond the tool radius.</summary>
    public double OverhangMm { get; init; } = 2;
}

/// <summary>
/// Planfräsa offerskivan (wasteboard surfacing): turns the settings into an ordinary project – the area
/// as material on the machine table, one rectangle and a facing operation over it – so it goes through
/// the same CAM checks, G-code verification and run as every other project, and can be saved and used
/// again next time. X0 Y0 is the area's front-left corner and Z0 the wasteboard's top.
/// </summary>
public static class WasteboardSurfacing
{
    public const string MaterialName = "Offerskiva";

    /// <summary>
    /// How far outside the area the cutter reaches, plus the margin to the limit switches: each row's end
    /// puts the tool's centre a radius plus the run-out past the edge, and the whole cutter (one more
    /// radius) must stay that margin inside the machine – as the CAM check requires.
    /// </summary>
    public static double Reach(MachineProfile m, SurfacingSettings s) => s.ToolDiameter + s.OverhangMm + Math.Max(0, m.LimitMarginMm);

    /// <summary>The largest area the machine can face: the travel minus <see cref="Reach"/> on both sides.</summary>
    public static (double Width, double Length) MaxArea(MachineProfile m, SurfacingSettings s)
    {
        double edge = 2 * Reach(m, s) + 1;   // + 1 mm so rounding never touches the limit
        return (Math.Floor(Math.Max(0, m.WorkWidthMm - edge)), Math.Floor(Math.Max(0, m.WorkLengthMm - edge)));
    }

    /// <summary>The reason the settings cannot become a project, or null.</summary>
    public static string? Validate(MachineProfile m, SurfacingSettings s)
    {
        if (!double.IsFinite(s.ToolDiameter) || s.ToolDiameter <= 0 || s.ToolDiameter > 100) return "Fräsdiametern måste vara större än 0 och högst 100 mm.";
        if (!double.IsFinite(s.WasteboardThickness) || s.WasteboardThickness <= 0 || s.WasteboardThickness > 200) return "Ange offerskivans tjocklek i mm.";
        if (!double.IsFinite(s.TotalDepth) || s.TotalDepth <= 0) return "Djupet måste vara större än 0.";
        if (s.TotalDepth > 5) return "Planfräsa högst 5 mm åt gången – offerskivan ska bara göras plan, inte bytas.";
        if (s.TotalDepth >= s.WasteboardThickness) return "Djupet måste vara mindre än offerskivans tjocklek.";
        if (!double.IsFinite(s.DepthPerPass) || s.DepthPerPass <= 0) return "Djup per pass måste vara större än 0.";
        var (maxW, maxL) = MaxArea(m, s);
        double w = s.Width ?? maxW, l = s.Length ?? maxL;
        if (!double.IsFinite(w) || !double.IsFinite(l) || w < 10 || l < 10) return "Ytan måste vara minst 10 × 10 mm.";
        if (w > maxW + 1e-9 || l > maxL + 1e-9)
            return $"Ytan får vara högst {maxW:0} × {maxL:0} mm: raderna går ut förbi kanten så att hela fräsen lämnar ytan (fräsens diameter + {s.OverhangMm:0.#} mm), och {m.LimitMarginMm:0.#} mm ska finnas kvar till gränslägena.";
        return null;
    }

    /// <summary>The surfacing project. Throws if <see cref="Validate"/> has a reason not to.</summary>
    public static CncDocument CreateProject(MachineProfile machine, SurfacingSettings s)
    {
        if (Validate(machine, s) is string error) throw new ArgumentException(error);
        var (maxW, maxL) = MaxArea(machine, s);
        double w = s.Width ?? maxW, l = s.Length ?? maxL;
        var doc = new CncDocument
        {
            Name = $"Planfräsa offerskivan {w:0} × {l:0}",
            MaterialWidth = w, MaterialHeight = l, MaterialThickness = s.WasteboardThickness,
            HasMaterial = true, MaterialName = MaterialName, WorkZero = WorkZero.FrontLeft,
            Machine = MachineLibrary.Clone(machine)
        };
        doc.Motion.EndPosition = EndPosition.WorkZero;
        // Where the area lies on the table: from the home corner as asked, or centred. Canvas coordinates
        // run from the table's back-left corner with Y towards the front (as the machine view draws it).
        double reach = Reach(machine, s);
        double tx = s.FromHomeX ?? (machine.WorkWidthMm - w) / 2, ty = s.FromHomeY ?? (machine.WorkLengthMm - l) / 2;
        tx = Math.Clamp(tx, reach, Math.Max(reach, machine.WorkWidthMm - w - reach));
        ty = Math.Clamp(ty, reach, Math.Max(reach, machine.WorkLengthMm - l - reach));
        bool left = machine.HomeCorner is HomeCorner.BackLeft or HomeCorner.FrontLeft;
        bool front = machine.HomeCorner is HomeCorner.FrontLeft or HomeCorner.FrontRight;
        doc.Stock.X = left ? tx : machine.WorkWidthMm - tx - w;
        doc.Stock.Y = front ? machine.WorkLengthMm - ty - l : ty;

        var rect = new DesignShape { Kind = ShapeKind.Rectangle, X = 0, Y = 0, Width = w, Height = l };
        doc.Shapes.Add(rect);
        doc.CamOperations.Add(new CamOperation
        {
            ShapeId = rect.Id, Kind = CamOperationKind.Face, ToolName = s.ToolName, ToolKind = ToolKind.FlatEndMill,
            ToolDiameter = s.ToolDiameter, TotalDepth = s.TotalDepth, DepthPerPass = s.DepthPerPass, UseTabs = false,
            StepoverPercent = s.StepoverPercent, FaceOverhangMm = s.OverhangMm, FaceAlongY = s.RowsAlongY,
            FeedRate = s.FeedRate, PlungeRate = s.PlungeRate, SpindleRpm = s.SpindleRpm
        });
        return doc;
    }

    /// <summary>
    /// Where X0 Y0 (the area's front-left corner) is, counted from the home corner as the tool marker in the
    /// machine view shows it – jog until the marker shows these numbers, then zero X and Y.
    /// </summary>
    public static (double X, double Y) WorkZeroFromHome(CncDocument doc) => Grbl.WorkZeroPlacement.PointFromHome(doc);

    /// <summary>The area's nearest corner from the home corner, as the project places it (for the instructions).</summary>
    public static (double X, double Y) FromHome(CncDocument doc)
    {
        var m = doc.Machine;
        bool left = m.HomeCorner is HomeCorner.BackLeft or HomeCorner.FrontLeft;
        bool front = m.HomeCorner is HomeCorner.FrontLeft or HomeCorner.FrontRight;
        return (left ? doc.Stock.X : m.WorkWidthMm - (doc.Stock.X + doc.MaterialWidth),
                front ? m.WorkLengthMm - (doc.Stock.Y + doc.MaterialHeight) : doc.Stock.Y);
    }
}
