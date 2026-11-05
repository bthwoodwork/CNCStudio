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

public enum CamOperationKind { Outside, Inside, Pocket }

public sealed class CamOperation
{
    public Guid ShapeId { get; set; }
    public CamOperationKind Kind { get; set; } = CamOperationKind.Outside;
    public double ToolDiameter { get; set; } = 3.175;
    public double TotalDepth { get; set; } = 6;
    public double DepthPerPass { get; set; } = 2;
    public bool UseTabs { get; set; } = true;
}

public static class CamValidation
{
    public static string? GetError(CamOperation operation, double materialThickness)
    {
        if (!double.IsFinite(operation.ToolDiameter) || operation.ToolDiameter <= 0 || operation.ToolDiameter > 100)
            return "Fräsdiameter måste vara större än 0 och högst 100 mm.";
        if (!double.IsFinite(operation.TotalDepth) || operation.TotalDepth <= 0 || operation.TotalDepth > materialThickness)
            return "Totalt djup måste vara större än 0 och får inte överstiga materialtjockleken.";
        if (!double.IsFinite(operation.DepthPerPass) || operation.DepthPerPass <= 0 || operation.DepthPerPass > operation.TotalDepth)
            return "Djup per pass måste vara större än 0 och får inte överstiga totalt djup.";
        if (operation.Kind == CamOperationKind.Pocket && operation.UseTabs)
            return "Hållflikar används inte för fickfräsning.";
        return null;
    }
}

// Machine-space coordinates in millimetres use a normalized top-left screen convention.
// A physical GRBL MPos mapping must be calibrated separately before machine use.
public sealed class MachineProfile
{
    public string Name { get; set; } = "Maskinprofil (ej verifierad)";
    public double WorkWidthMm { get; set; } = 750;
    public double WorkLengthMm { get; set; } = 750;
    public bool CalibratedToGrbl { get; set; } = false;
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
    public static string? Validate(CncDocument d)
    {
        var m = d.Machine; var stock = d.Stock;
        if (m is null || stock is null || d.Fixtures is null) return "Maskininställningar saknas.";
        if (!double.IsFinite(m.WorkWidthMm) || !double.IsFinite(m.WorkLengthMm) ||
            m.WorkWidthMm <= 0 || m.WorkLengthMm <= 0 || m.WorkWidthMm > 10000 || m.WorkLengthMm > 10000)
            return "Maskinens arbetsområde måste ha giltiga mått.";
        if (!double.IsFinite(stock.X) || !double.IsFinite(stock.Y) || stock.X < 0 || stock.Y < 0 ||
            stock.X + d.MaterialWidth > m.WorkWidthMm + 0.00001 ||
            stock.Y + d.MaterialHeight > m.WorkLengthMm + 0.00001)
            return "Materialet ligger utanför maskinens planerade arbetsområde.";
        foreach (var f in d.Fixtures)
        {
            if (!double.IsFinite(f.X) || !double.IsFinite(f.Y) ||
                !double.IsFinite(f.Width) || !double.IsFinite(f.Length) ||
                !double.IsFinite(f.HeightAboveStockMm) || !double.IsFinite(f.ClearanceMm) ||
                f.Width <= 0 || f.Length <= 0 || f.HeightAboveStockMm < 0 || f.ClearanceMm < 0 ||
                f.X < 0 || f.Y < 0 || f.X + f.Width > m.WorkWidthMm + .00001 ||
                f.Y + f.Length > m.WorkLengthMm + .00001)
                return $"Fastspänningen '{f.Name}' har ogiltig placering eller mått.";
        }
        return null;
    }
}

public sealed class CncDocument
{
    public string Name { get; set; } = "Nytt projekt";
    public MachineProfile Machine { get; set; } = new();
    public StockPlacement Stock { get; set; } = new();
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
    public static void Save(string path, CncDocument document) => File.WriteAllText(path, JsonSerializer.Serialize(document, Options));
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
        document.Fixtures ??= new List<FixtureBlock>();
        document.Camera ??= new CameraSetup();
        if (MachineSetupValidation.Validate(document) is string setupError)
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
