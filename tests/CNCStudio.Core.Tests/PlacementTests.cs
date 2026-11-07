using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>A smaller work area must not stop saving settings – only the toolpaths.</summary>
public class PlacementTests
{
    private static CncDocument DocOn750WithStockAt(double x)
    {
        var doc = new CncDocument { MaterialWidth = 300, MaterialHeight = 200 };
        doc.Machine.WorkWidthMm = 750; doc.Machine.WorkLengthMm = 750;
        doc.Stock = new StockPlacement { X = x, Y = 50 };
        doc.Motion.StockTopMachineZ = -40;
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 50, Y = 50, Width = 100, Height = 60 };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id });
        return doc;
    }

    [Fact]
    public void Machine_profile_is_valid_even_if_the_old_material_no_longer_fits()
    {
        var doc = DocOn750WithStockAt(400);                               // 400 + 300 = 700 mm: fine on 750
        doc.Machine.WorkWidthMm = 500; doc.Machine.WorkLengthMm = 500;     // the real machine is smaller
        Assert.Null(MachineSetupValidation.ValidateProfile(doc.Machine));
        Assert.Contains("utanför", MachineSetupValidation.PlacementProblem(doc));
        Assert.NotNull(MachineSetupValidation.StockProblem(doc));
    }

    [Fact]
    public void Material_outside_the_work_area_blocks_every_toolpath_and_export()
    {
        var doc = DocOn750WithStockAt(400);
        doc.Machine.WorkWidthMm = 500;
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        var op = Assert.Single(report.Operations);
        Assert.Null(op.Preview);
        Assert.Contains(op.Findings, f => f.Severity == CamSeverity.Critical && f.Code == "PlacementOutsideMachine");
        Assert.Contains(GCodeGenerator.Blockers(doc, report), b => b.FindingCode == "PlacementOutsideMachine");
        Assert.False(GCodeGenerator.Generate(doc, report).Success);
    }

    [Fact]
    public void Moving_the_material_inside_again_brings_the_toolpaths_back()
    {
        var doc = DocOn750WithStockAt(400);
        doc.Machine.WorkWidthMm = 500;
        doc.Stock = new StockPlacement { X = 50, Y = 50 };
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        Assert.NotNull(Assert.Single(report.Operations).Preview);
    }

    [Fact]
    public void A_project_with_material_outside_the_work_area_can_be_saved_and_opened()
    {
        var doc = DocOn750WithStockAt(400);
        doc.Machine.WorkWidthMm = 500;
        var path = Path.Combine(Path.GetTempPath(), "cncstudio-placement-" + Guid.NewGuid().ToString("N") + ".cncstudio.json");
        DocumentStorage.Save(path, doc);
        var loaded = DocumentStorage.Load(path);
        Assert.Equal(400, loaded.Stock.X);
        Assert.NotNull(MachineSetupValidation.StockProblem(loaded));
    }

    [Fact]
    public void An_invalid_profile_is_still_refused()
    {
        Assert.NotNull(MachineSetupValidation.ValidateProfile(new MachineProfile { HomingPullOffMm = 5, LimitMarginMm = 3 }));
    }
}
