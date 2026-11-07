using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>A new project has no material until the user adds it.</summary>
public class NoMaterialTests
{
    private static CncDocument Doc(bool hasMaterial)
    {
        var doc = new CncDocument { HasMaterial = hasMaterial };
        doc.Motion.StockTopMachineZ = -40;
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 50, Y = 50, Width = 100, Height = 60 };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, UseTabs = false });
        return doc;
    }

    [Fact]
    public void Without_material_no_toolpath_is_computed_and_export_is_blocked()
    {
        var doc = Doc(hasMaterial: false);
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        var op = Assert.Single(report.Operations);
        Assert.Null(op.Preview);
        Assert.Contains(op.Findings, f => f.Code == "NoMaterial" && f.Severity == CamSeverity.Critical);
        Assert.Contains(GCodeGenerator.Blockers(doc, report), b => b.FindingCode == "NoMaterial");
    }

    [Fact]
    public void Adding_material_brings_the_toolpaths()
    {
        var doc = Doc(hasMaterial: false);
        var planner = new CamPlanner(ContourToolpaths.Generate);
        Assert.Null(planner.Review(doc, computeAll: true).Operations[0].Preview);
        doc.HasMaterial = true;
        Assert.NotNull(planner.Review(doc, computeAll: true).Operations[0].Preview);
    }

    [Fact]
    public void Placement_is_not_checked_for_material_that_is_not_there()
    {
        var doc = Doc(hasMaterial: false);
        doc.MaterialWidth = 5000;                                     // a placeholder must not count
        Assert.Null(MachineSetupValidation.PlacementProblem(doc));
    }

    [Fact]
    public void A_project_without_material_saves_and_loads_as_such_and_old_files_have_material()
    {
        var path = Path.Combine(Path.GetTempPath(), "cncstudio-nomat-" + Guid.NewGuid().ToString("N") + ".cncstudio.json");
        DocumentStorage.Save(path, Doc(hasMaterial: false));
        Assert.False(DocumentStorage.Load(path).HasMaterial);

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"HasMaterial\"", "\"Gammal\""));
        Assert.True(DocumentStorage.Load(path).HasMaterial);           // files from before 0.8.8
    }
}
