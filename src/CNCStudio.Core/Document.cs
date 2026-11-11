using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNCStudio.Core;

public enum ShapeKind { Rectangle, Ellipse, VectorPath }

// Points are normalized to each shape's bounding rectangle (0..1).
// Multiple contours support disjoint islands and holes (even-odd fill).
public sealed class VectorContour
{
    public bool Closed { get; set; } = true;
    public List<VectorPoint> Points { get; set; } = new();
}

public sealed class VectorPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public VectorPoint() { }
    public VectorPoint(double x, double y) { X = x; Y = y; }
}


public sealed class DesignShape
{
    public Guid Id { get; set; } = Guid.NewGuid();
    // Members with the same non-null GroupId are selected and moved together.
    public Guid? GroupId { get; set; }
    public ShapeKind Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public List<VectorContour> Contours { get; set; } = new();
}

/// <summary>Drill: straight plunges at the shape's centre (the hole gets the tool's diameter), in pecks of DepthPerPass.</summary>
/// <para>Face: the shape's whole area cut flat in rows, running out past the edges (planfräsning).</para>
public enum CamOperationKind { Outside, Inside, Pocket, Drill, Face }

public enum PlungeMode { Vertical, Ramp }

/// <summary>
/// Climb: with a clockwise spindle (M3) the finished wall is on the tool's right in the direction
/// of travel. Conventional: on the left. Default: keep the generated direction.
/// </summary>
public enum MillingDirection { Default, Climb, Conventional }

public enum ToolKind { FlatEndMill, BallNose, VBit }

/// <summary>Where G54 X0 Y0 lies on the material. Z0 is always the top surface.</summary>
public enum WorkZero { FrontLeft, Center }

public sealed class CamOperation
{
    /// <summary>Name of the tool from the library (for the file header and the user); informational.</summary>
    public string? ToolName { get; set; }
    public ToolKind ToolKind { get; set; } = ToolKind.FlatEndMill;
    public PlungeMode Plunge { get; set; } = PlungeMode.Vertical;
    /// <summary>Ramp angle in degrees from horizontal.</summary>
    public double RampAngleDeg { get; set; } = 3;
    /// <summary>Feed along the ramp, as % of <see cref="FeedRate"/> – the tool cuts with its tip and side at once, so slower is gentler.</summary>
    public double RampFeedPercent { get; set; } = 70;
    /// <summary>The ramp's feed in mm/min.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double RampFeedRate => FeedRate * RampFeedPercent / 100;
    public MillingDirection Direction { get; set; } = MillingDirection.Default;
    public Guid ShapeId { get; set; }
    public CamOperationKind Kind { get; set; } = CamOperationKind.Outside;
    public double ToolDiameter { get; set; } = 3.175;
    public double TotalDepth { get; set; } = 6;
    public double DepthPerPass { get; set; } = 2;
    public bool UseTabs { get; set; } = true;
    /// <summary>Tabs per closed path (fewer are placed if the path is too short).</summary>
    public int TabCount { get; set; } = 4;
    /// <summary>Width of the material bridge left at each tab, in mm (measured at the part).</summary>
    public double TabWidth { get; set; } = 6;
    /// <summary>Height of the tab above the bottom of the cut, in mm.</summary>
    public double TabHeight { get; set; } = 3;
    /// <summary>Cutting feed rate in mm/min (G1 F, G94).</summary>
    public double FeedRate { get; set; } = 1000;
    /// <summary>Plunge (Z feed) rate in mm/min, also used to lift over tabs.</summary>
    public double PlungeRate { get; set; } = 300;
    /// <summary>Spindle speed for M3 S, in RPM. Mapped by GRBL to PWM via $30/$31.</summary>
    public double SpindleRpm { get; set; } = 18000;

    /// <summary>Facing: how far past the edge each row runs, in addition to the tool radius (the cutter leaves the material).</summary>
    public double FaceOverhangMm { get; set; } = 2;
    /// <summary>Facing: rows along Y instead of along X.</summary>
    public bool FaceAlongY { get; set; }

    /// <summary>Pocket and facing: distance between neighbouring loops/rows, as % of the tool diameter.</summary>
    public double StepoverPercent { get; set; } = 40;

    /// <summary>A copy with every setting, for another shape (used when duplicating).</summary>
    public CamOperation CopyFor(Guid shapeId)
    {
        var copy = (CamOperation)MemberwiseClone();
        copy.ShapeId = shapeId;
        return copy;
    }
}

public static class CamValidation
{
    public static string? GetError(CamOperation operation, double materialThickness)
    {
        if (!double.IsFinite(operation.ToolDiameter) || operation.ToolDiameter <= 0 || operation.ToolDiameter > 100)
            return "Fräsdiameter måste vara större än 0 och högst 100 mm.";
        if (!double.IsFinite(operation.TotalDepth) || operation.TotalDepth <= 0 || operation.TotalDepth > materialThickness)
            return "Totalt djup måste vara större än 0 och får inte överstiga materialtjockleken.";
        // A pass deeper than the total depth simply means one pass (e.g. the tool's cutting data says 3 mm
        // per pass and this cut is only 2 mm deep).
        if (!double.IsFinite(operation.DepthPerPass) || operation.DepthPerPass <= 0)
            return "Djup per pass måste vara större än 0.";
        if (operation.Plunge == PlungeMode.Ramp &&
            (!double.IsFinite(operation.RampAngleDeg) || operation.RampAngleDeg < 1 || operation.RampAngleDeg > 45))
            return "Rampvinkeln måste vara 1–45 grader.";
        if (operation.Plunge == PlungeMode.Ramp &&
            (!double.IsFinite(operation.RampFeedPercent) || operation.RampFeedPercent < 5 || operation.RampFeedPercent > 100))
            return "Rampens matning måste vara 5–100 % av matningen.";
        if (operation.Kind == CamOperationKind.Pocket && operation.UseTabs)
            return "Hållflikar används inte för fickfräsning.";
        if (operation.Kind == CamOperationKind.Drill && operation.UseTabs)
            return "Hållflikar används inte för borrning.";
        if (operation.Kind == CamOperationKind.Face && operation.UseTabs)
            return "Hållflikar används inte för planfräsning.";
        if (operation.Kind == CamOperationKind.Face &&
            (!double.IsFinite(operation.StepoverPercent) || operation.StepoverPercent < 5 || operation.StepoverPercent > 95))
            return "Stegningen för planfräsning måste vara 5–95 % av fräsdiametern.";
        if (operation.Kind == CamOperationKind.Face &&
            (!double.IsFinite(operation.FaceOverhangMm) || operation.FaceOverhangMm < 0 || operation.FaceOverhangMm > 50))
            return "Utgången utanför kanten måste vara 0–50 mm.";
        if (operation.Kind == CamOperationKind.Pocket &&
            (!double.IsFinite(operation.StepoverPercent) || operation.StepoverPercent < 5 || operation.StepoverPercent > 90))
            return "Stegningen för fickfräsning måste vara 5–90 % av fräsdiametern.";
        if (!double.IsFinite(operation.FeedRate) || operation.FeedRate < 1 || operation.FeedRate > 20000)
            return "Matningen måste vara 1–20000 mm/min.";
        if (!double.IsFinite(operation.PlungeRate) || operation.PlungeRate < 1 || operation.PlungeRate > 20000)
            return "Nedmatningen måste vara 1–20000 mm/min.";
        if (!double.IsFinite(operation.SpindleRpm) || operation.SpindleRpm < 0 || operation.SpindleRpm > 60000)
            return "Spindelvarvtalet måste vara 0–60000 varv/min.";
        if (operation.UseTabs)
        {
            if (operation.TabCount < 1 || operation.TabCount > 50)
                return "Antal hållflikar måste vara mellan 1 och 50.";
            if (!double.IsFinite(operation.TabWidth) || operation.TabWidth <= 0 || operation.TabWidth > 100)
                return "Flikbredd måste vara större än 0 och högst 100 mm.";
            if (!double.IsFinite(operation.TabHeight) || operation.TabHeight <= 0 || operation.TabHeight > 100)
                return "Flikhöjd måste vara större än 0 och högst 100 mm.";
        }
        return null;
    }
}

// Machine-space coordinates in millimetres use a normalized top-left screen convention.
// A physical GRBL MPos mapping must be calibrated separately before machine use.
public enum HomeCorner { BackLeft, BackRight, FrontLeft, FrontRight }

/// <summary>How the spindle/router is switched on and its speed set.</summary>
public enum SpindleControl
{
    /// <summary>GRBL switches the spindle (M3 S… / M5).</summary>
    Grbl,
    /// <summary>Router started and speed set by hand; the program pauses with M0 instead.</summary>
    Manual
}

public sealed class MachineProfile
{
    public string Name { get; set; } = "Maskinprofil (ej verifierad)";
    /// <summary>Usable X travel (GRBL $130).</summary>
    public double WorkWidthMm { get; set; } = 750;
    /// <summary>Usable Y travel (GRBL $131).</summary>
    public double WorkLengthMm { get; set; } = 750;
    /// <summary>Usable Z travel from the top switch downwards (GRBL $132).</summary>
    public double ZTravelMm { get; set; } = 65;
    /// <summary>
    /// The machine is homed with $H after every startup (requires $22=1 in GRBL, which keeps GRBL in
    /// alarm until homed). Required for known machine coordinates and the Z switch position.
    /// </summary>
    public bool HomeOnStartup { get; set; } = true;
    /// <summary>
    /// GRBL $3, direction port invert mask: bit 1 = X, bit 2 = Y, bit 4 = Z. Verified (wiki, stepper.c):
    /// it only flips the electrical direction signal so an axis physically moves the other way; machine
    /// coordinates are unaffected. Set it so +X moves right, +Y away from the operator and +Z up
    /// (right-hand rule) before setting up homing. Not to be confused with $2 (step pulse invert).
    /// </summary>
    public int DirInvertMask { get; set; } = 0;

    /// <summary>
    /// GRBL $23, homing direction invert mask: bit 1 = X, bit 2 = Y, bit 4 = Z. Verified (GRBL 1.1 wiki):
    /// by default ($23=0) every axis homes in the positive direction; a set bit makes that axis home
    /// negative. Z must home positive (up), so bit 4 must be clear.
    /// </summary>
    public int HomingDirInvertMask { get; set; } = 3; // X/Y switches front-left (SW), as on the user's machine

    /// <summary>
    /// Corner where X/Y homing ends up, derived from $23. Assumes the usual orientation:
    /// +X to the right and +Y away from the operator (towards the back).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public HomeCorner HomeCorner =>
        ((HomingDirInvertMask & 1) != 0, (HomingDirInvertMask & 2) != 0) switch
        {
            (false, false) => HomeCorner.BackRight,
            (true, false) => HomeCorner.BackLeft,
            (false, true) => HomeCorner.FrontRight,
            (true, true) => HomeCorner.FrontLeft
        };

    /// <summary>
    /// Distance GRBL backs off a switch after homing (GRBL $27). Verified in limits.c: an axis that
    /// homes positive is set to MPos = −$27 after homing; the switch itself is at MPos 0.
    /// </summary>
    public double HomingPullOffMm { get; set; } = 1;
    /// <summary>GRBL soft limits enabled ($20=1).</summary>
    public bool SoftLimits { get; set; } = true;
    /// <summary>Planned moves keep at least this distance from every limit (XY edges, Z top and bottom).</summary>
    public double LimitMarginMm { get; set; } = 3;
    /// <summary>
    /// Manual router: no M3/S/M5 are written (they would have no effect); the program pauses with M0
    /// (resume with '~', verified in the GRBL wiki) so the router can be started and set by hand.
    /// </summary>
    public SpindleControl Spindle { get; set; } = SpindleControl.Manual;
    /// <summary>Maximum rates from GRBL $110/$111/$112 (mm/min; also the G0 rate). Null = not read.</summary>
    public double? MaxRateX { get; set; }
    public double? MaxRateY { get; set; }
    public double? MaxRateZ { get; set; }
    public bool CalibratedToGrbl { get; set; } = false;
    /// <summary>Which machine in the machine library (Inställningar\Maskiner) this profile belongs to. Null = not from the library (old project).</summary>
    public string? Id { get; set; }
    /// <summary>The port this machine was last connected on (COM3, or the demo port). Connecting there picks this machine.</summary>
    public string? Port { get; set; }

    public override string ToString() => Name;
}

/// <summary>
/// Z heights for moves between cuts. All values are mm above the stock top (work Z0 = stock top).
/// </summary>
public sealed class MotionSettings
{
    /// <summary>Normal height for rapid (G0) moves between cuts.</summary>
    public double TravelHeightMm { get; set; } = 10;
    /// <summary>Rapid down to this height above the surface before the feed plunge.</summary>
    public double PlungeClearanceMm { get; set; } = 2;
    /// <summary>
    /// Machine Z (MPos) at the stock top, i.e. where work Z0 is set. The machine always homes at
    /// startup and the Z limit switch is at the top, so after homing MPos Z = 0 is the top of travel
    /// and the stock top lies below it (negative). Null = not entered. Changes whenever Z0 is reset
    /// (new stock, new tool). Later read automatically from GRBL's G54 offset.
    /// </summary>
    public double? StockTopMachineZ { get; set; }

    /// <summary>How far above the stock top Z can travel before reaching home (the Z switch).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? ZMaxAboveStockMm => StockTopMachineZ is double z ? -z : null;

    /// <summary>
    /// Dwell after M3 before the first move, in seconds (G4 P is seconds in GRBL 1.1). Whether GRBL
    /// itself waits for spin-up is not confirmed, so an explicit dwell is always written.
    /// </summary>
    public double SpindleSpinUpSeconds { get; set; } = 4;

    /// <summary>
    /// When the tool goes back down into a cut that is already made (a pocket's next level, a new pass
    /// after a lift), it rapids (G0) down to this height above the depth already cut and feeds only the
    /// rest. Null = feed the whole way down from <see cref="PlungeClearanceMm"/>.
    /// </summary>
    public double? RapidIntoCutClearanceMm { get; set; } = 0.5;

    /// <summary>Where the tool goes when the program is done (at the start/end height).</summary>
    public EndPosition EndPosition { get; set; } = EndPosition.WorkZero;
    /// <summary>End point in work coordinates (mm) when <see cref="EndPosition"/> is <see cref="EndPosition.Point"/>.</summary>
    public double EndXMm { get; set; }
    public double EndYMm { get; set; }
}

/// <summary>Program end: stay above the last cut, go to work zero (X0 Y0) or to a chosen point.</summary>
public enum EndPosition { StayAbove, WorkZero, Point }

public static class MotionValidation
{
    public static string? GetError(MotionSettings m)
    {
        if (!double.IsFinite(m.PlungeClearanceMm) || m.PlungeClearanceMm <= 0 || m.PlungeClearanceMm > 50)
            return "Höjd före nedmatning måste vara större än 0 och högst 50 mm.";
        if (!double.IsFinite(m.TravelHeightMm) || m.TravelHeightMm < m.PlungeClearanceMm || m.TravelHeightMm > 200)
            return "Förflyttningshöjden måste vara minst lika stor som höjden före nedmatning och högst 200 mm.";
        if (m.StockTopMachineZ is double z && (!double.IsFinite(z) || z >= 0 || z < -500))
            return "Maskinens Z vid materialytan måste vara negativ (under Z-brytaren efter homing), högst −500 mm.";
        if (!double.IsFinite(m.SpindleSpinUpSeconds) || m.SpindleSpinUpSeconds < 0 || m.SpindleSpinUpSeconds > 60)
            return "Spindelns uppstartstid måste vara 0–60 sekunder.";
        if (m.RapidIntoCutClearanceMm is double rc && (!double.IsFinite(rc) || rc < 0.1 || rc > m.PlungeClearanceMm))
            return "Höjden över redan fräst djup måste vara minst 0,1 mm och högst höjden före nedmatning.";
        if (m.EndPosition == EndPosition.Point && (!double.IsFinite(m.EndXMm) || !double.IsFinite(m.EndYMm)))
            return "Slutpunktens X och Y måste vara tal.";
        return null;
    }
}

public sealed class StockPlacement
{
    public double X { get; set; } = 50;
    public double Y { get; set; } = 50;
}

public sealed class FixtureBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Fastspänning";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 30;
    public double Length { get; set; } = 20;
    // Heights are relative to the workpiece top, not machine Z.
    public double HeightAboveStockMm { get; set; } = 20;
    public double ClearanceMm { get; set; } = 5;
    public bool Locked { get; set; }
}

// Reserved for future camera integration; no vision detection or GRBL movement implemented.
public sealed class CameraSetup
{
    public bool CalibrationVerified { get; set; } = false;
    public string? DeviceId { get; set; }
}

public static class MachineSetupValidation
{
    /// <summary>Profile, stock and fixtures together (used where something is placed on the table).</summary>
    public static string? Validate(CncDocument d) => ValidateProfile(d.Machine) ?? PlacementProblem(d);

    /// <summary>
    /// The machine profile on its own. Machine settings can always be saved when this passes – even if
    /// material or fixtures placed earlier no longer fit a smaller work area (that only blocks CAM).
    /// </summary>
    public static string? ValidateProfile(MachineProfile? m)
    {
        if (m is null) return "Maskininställningar saknas.";
        if (!double.IsFinite(m.WorkWidthMm) || !double.IsFinite(m.WorkLengthMm) ||
            m.WorkWidthMm <= 0 || m.WorkLengthMm <= 0 || m.WorkWidthMm > 10000 || m.WorkLengthMm > 10000)
            return "Maskinens arbetsområde måste ha giltiga mått.";
        if (!double.IsFinite(m.ZTravelMm) || m.ZTravelMm <= 0 || m.ZTravelMm > 500)
            return "Z-axelns rörelse måste vara större än 0 och högst 500 mm.";
        if (!double.IsFinite(m.HomingPullOffMm) || m.HomingPullOffMm < 0 || m.HomingPullOffMm > 20)
            return "Pull-off efter homing måste vara 0–20 mm.";
        if (!double.IsFinite(m.LimitMarginMm) || m.LimitMarginMm < 0 || m.LimitMarginMm > 50)
            return "Säkerhetsmarginalen till gränslägena måste vara 0–50 mm.";
        if (m.LimitMarginMm < m.HomingPullOffMm)
            return $"Marginalen till gränslägena ({m.LimitMarginMm:0.###} mm) måste vara minst pull-off ($27 = " +
                   $"{m.HomingPullOffMm:0.###} mm). Annars kan rörelser komma närmare brytarna än där homing stannar.";
        if (m.DirInvertMask < 0 || m.DirInvertMask > 7)
            return "$3 (axelriktning) måste vara ett heltal 0–7.";
        if (m.HomingDirInvertMask < 0 || m.HomingDirInvertMask > 7)
            return "$23 (homingriktning) måste vara ett heltal 0–7.";
        if ((m.HomingDirInvertMask & 4) != 0)
            return "$23 har bit 4 (Z) satt, så Z homar nedåt. CNC Studio kräver att Z homar uppåt mot en brytare upptill.";
        if (string.IsNullOrWhiteSpace(m.Name))
            return "Ange ett namn på maskinprofilen.";
        return null;
    }

    /// <summary>Fixtures with impossible values (not about where they are) – checked when loading a project.</summary>
    public static string? FixtureValues(CncDocument d)
    {
        if (d.Stock is null || d.Fixtures is null) return "Maskininställningar saknas.";
        if (!double.IsFinite(d.Stock.X) || !double.IsFinite(d.Stock.Y)) return "Materialets placering är ogiltig.";
        foreach (var f in d.Fixtures)
            if (!double.IsFinite(f.X) || !double.IsFinite(f.Y) || !double.IsFinite(f.Width) || !double.IsFinite(f.Length) ||
                !double.IsFinite(f.HeightAboveStockMm) || !double.IsFinite(f.ClearanceMm) ||
                f.Width <= 0 || f.Length <= 0 || f.HeightAboveStockMm < 0 || f.ClearanceMm < 0)
                return $"Fastspänningen '{f.Name}' har ogiltiga mått.";
        return null;
    }

    /// <summary>Material or a fixture outside the machine's work area, or null. Blocks toolpaths, not saving.</summary>
    public static string? PlacementProblem(CncDocument d)
    {
        var m = d.Machine; var stock = d.Stock;
        if (m is null || stock is null || d.Fixtures is null) return "Maskininställningar saknas.";
        if (FixtureValues(d) is string bad) return bad;
        return (d.HasMaterial ? StockProblem(d) : null) ?? d.Fixtures.Select(f => FixtureProblem(d, f)).FirstOrDefault(p => p is not null);
    }

    /// <summary>The material outside the work area, or null.</summary>
    public static string? StockProblem(CncDocument d)
    {
        var m = d.Machine; var stock = d.Stock;
        if (m is null || stock is null) return "Maskininställningar saknas.";
        if (!double.IsFinite(stock.X) || !double.IsFinite(stock.Y) || stock.X < 0 || stock.Y < 0 ||
            stock.X + d.MaterialWidth > m.WorkWidthMm + 0.00001 ||
            stock.Y + d.MaterialHeight > m.WorkLengthMm + 0.00001)
            return $"Materialet ({d.MaterialWidth:0.#} × {d.MaterialHeight:0.#} mm) ligger utanför maskinens arbetsområde " +
                   $"({m.WorkWidthMm:0.#} × {m.WorkLengthMm:0.#} mm).";
        return null;
    }

    /// <summary>One fixture with impossible values or outside the work area, or null.</summary>
    public static string? FixtureProblem(CncDocument d, FixtureBlock f)
    {
        var m = d.Machine;
        if (m is null) return "Maskininställningar saknas.";
        if (!double.IsFinite(f.X) || !double.IsFinite(f.Y) || !double.IsFinite(f.Width) || !double.IsFinite(f.Length) ||
            !double.IsFinite(f.HeightAboveStockMm) || !double.IsFinite(f.ClearanceMm) ||
            f.Width <= 0 || f.Length <= 0 || f.HeightAboveStockMm < 0 || f.ClearanceMm < 0)
            return $"Fastspänningen '{f.Name}' har ogiltiga mått.";
        if (f.X < 0 || f.Y < 0 || f.X + f.Width > m.WorkWidthMm + .00001 || f.Y + f.Length > m.WorkLengthMm + .00001)
            return $"Fastspänningen '{f.Name}' ligger utanför maskinens arbetsområde.";
        return null;
    }
}

public sealed class CncDocument
{
    public string Name { get; set; } = "Nytt projekt";
    /// <summary>
    /// False for a new project until material is added: nothing is drawn as stock and no toolpaths are
    /// computed. The size fields then only hold placeholders. Older project files always have material.
    /// </summary>
    public bool HasMaterial { get; set; } = true;
    /// <summary>The material from the material library (decides which cutting data tools use).</summary>
    public Guid? MaterialId { get; set; }
    /// <summary>The material's name, kept so the project still says what it is on a computer without that material.</summary>
    public string? MaterialName { get; set; }
    public MachineProfile Machine { get; set; } = new();
    public StockPlacement Stock { get; set; } = new();
    public MotionSettings Motion { get; set; } = new();
    public WorkZero WorkZero { get; set; } = WorkZero.FrontLeft;

    /// <summary>
    /// Converts design coordinates (Y down from the back edge) to work coordinates (G54, Y up),
    /// with X0 Y0 at the front-left corner or the centre of the material.
    /// </summary>
    public (double X, double Y) ToWork(double x, double y)
    {
        double wx = x, wy = MaterialHeight - y;
        if (WorkZero == WorkZero.Center) { wx -= MaterialWidth / 2; wy -= MaterialHeight / 2; }
        return (wx, wy);
    }
    /// <summary>Inverse of <see cref="ToWork"/>: work coordinates → stock-local design coordinates.</summary>
    public (double X, double Y) FromWork(double wx, double wy)
    {
        if (WorkZero == WorkZero.Center) { wx += MaterialWidth / 2; wy += MaterialHeight / 2; }
        return (wx, MaterialHeight - wy);
    }
    public List<FixtureBlock> Fixtures { get; set; } = new();
    public CameraSetup Camera { get; set; } = new();
    public double MaterialWidth { get; set; } = 600;
    public double MaterialHeight { get; set; } = 600;
    public double MaterialThickness { get; set; } = 18;
    public List<DesignShape> Shapes { get; set; } = new();
    public List<CamOperation> CamOperations { get; set; } = new();
    public InlaySettings? Inlay { get; set; }
}

public static class DocumentStorage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    /// <summary>Written via a temporary file, so a sync client (Google Drive) never uploads half a project.</summary>
    public static void Save(string path, CncDocument document) => AppStorage.WriteAtomic(path, JsonSerializer.Serialize(document, Options));
    public static CncDocument Load(string path)
    {
        var document = JsonSerializer.Deserialize<CncDocument>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Projektfilen är tom eller ogiltig.");
        if (document.MaterialWidth <= 0 || document.MaterialHeight <= 0 || document.MaterialThickness <= 0)
            throw new InvalidDataException("Ogiltiga materialmått i projektfilen.");
        if (document.Shapes is null || document.Shapes.Any(s => !double.IsFinite(s.X) || !double.IsFinite(s.Y) ||
            !double.IsFinite(s.Width) || !double.IsFinite(s.Height) || s.Width <= 0 || s.Height <= 0 || (s.Kind == ShapeKind.VectorPath &&
                (s.Contours is null || s.Contours.Count == 0 || s.Contours.Any(c => c.Points is null || c.Points.Count < (c.Closed ? 3 : 2) ||
                    c.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) ||
                        p.X < -0.00001 || p.X > 1.00001 || p.Y < -0.00001 || p.Y > 1.00001))))))
            throw new InvalidDataException("Ogiltig geometri i projektfilen.");
        document.Machine ??= new MachineProfile();
        document.Stock ??= new StockPlacement();
        document.Motion ??= new MotionSettings();
        if (MotionValidation.GetError(document.Motion) is string motionError)
            throw new InvalidDataException("Ogiltiga förflyttningsinställningar: " + motionError);
        document.Fixtures ??= new List<FixtureBlock>();
        document.Camera ??= new CameraSetup();
        if ((MachineSetupValidation.ValidateProfile(document.Machine) ?? MachineSetupValidation.FixtureValues(document)) is string setupError)
            throw new InvalidDataException(setupError);
        document.CamOperations ??= new();
        if (document.Inlay is not null && InlayValidation.GetError(document.Inlay, document.MaterialThickness) is string inlayError)
            throw new InvalidDataException("Ogiltiga V-carve inlay-inställningar: " + inlayError);
        if (document.CamOperations.Any(o => document.Shapes.Any(sh => sh.Id == o.ShapeId &&
            sh.Kind == ShapeKind.VectorPath && sh.Contours.Any(c => !c.Closed))))
            throw new InvalidDataException("Öppna vektorkonturer kan inte ha CAM-operationer.");
        if (document.CamOperations.Any(o => document.Shapes.All(shape => shape.Id != o.ShapeId) ||
                CamValidation.GetError(o, document.MaterialThickness) is not null) ||
            document.CamOperations.GroupBy(o => o.ShapeId).Any(group => group.Count() > 1))
            throw new InvalidDataException("Projektet innehåller ogiltiga CAM-inställningar.");
        return document;
    }
}
