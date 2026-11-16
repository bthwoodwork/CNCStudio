using CNCStudio.Core;
using Xunit;

public class SetupSheetTests
{
    [Fact]
    public void The_sheet_has_the_material_zero_programs_and_drawing()
    {
        var doc = new CncDocument { Name = "Skylt <test>", MaterialWidth = 300, MaterialHeight = 60, MaterialThickness = 18, HasMaterial = true };
        var hole = new DesignShape { Kind = ShapeKind.Rectangle, X = 20, Y = 10, Width = 40, Height = 30 };
        var outline = new DesignShape { Kind = ShapeKind.Ellipse, X = 100, Y = 5, Width = 50, Height = 50 };
        doc.Shapes.Add(hole); doc.Shapes.Add(outline);
        doc.CamOperations.Add(new CamOperation { ShapeId = hole.Id, Kind = CamOperationKind.Pocket, ToolDiameter = 6, TotalDepth = 5, DepthPerPass = 2.5, UseTabs = false, ToolName = "Rak 6 mm" });
        doc.CamOperations.Add(new CamOperation { ShapeId = outline.Id, Kind = CamOperationKind.Inside, ToolDiameter = 3, TotalDepth = 4, DepthPerPass = 2, UseTabs = false, ToolName = "Rak 3 mm" });
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);

        var html = SetupSheet.Html(doc, report, new DateTime(2026, 11, 16, 10, 0, 0));
        Assert.Contains("Skylt &lt;test&gt;", html);                          // text is escaped
        Assert.Contains("300 × 60 × 18 mm", html);
        Assert.Contains("Program 1 av 2", html);
        Assert.Contains("Program 2 av 2", html);
        Assert.Contains("Nolla Z på materialytan", html);                       // the tool change step for program 2
        Assert.Contains("<svg", html);
        Assert.Contains("<polyline", html);                                     // the toolpaths are drawn
        Assert.Contains("2026-11-16 10:00", html);
    }
}
