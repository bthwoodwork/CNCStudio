namespace CNCStudio.Core.Tests;

/// <summary>The operations tree: one program per tool in run order, status per operation, unassigned objects.</summary>
public class CamTreeTests
{
    private static DesignShape Add(CncDocument doc, ShapeKind kind, double x, double y, double w, double h, CamOperation? op)
    {
        var s = new DesignShape { Kind = kind, X = x, Y = y, Width = w, Height = h };
        doc.Shapes.Add(s);
        if (op is not null) { op.ShapeId = s.Id; doc.CamOperations.Add(op); }
        return s;
    }

    private static CncDocument Box()
    {
        var doc = new CncDocument { MaterialWidth = 170, MaterialHeight = 145, MaterialThickness = 21 };
        doc.Motion.StockTopMachineZ = -40;
        // Outside contour first in the document, holes after: the tree must still show the holes first.
        Add(doc, ShapeKind.Rectangle, 3, 10, 164, 125, new CamOperation { ToolName = "6 mm rak fräs", ToolDiameter = 6, Kind = CamOperationKind.Outside,
            TotalDepth = 21, DepthPerPass = 3, UseTabs = true, TabHeight = 2.3 });
        Add(doc, ShapeKind.Ellipse, 15, 115, 5, 5, new CamOperation { ToolName = "5 mm borr", ToolDiameter = 5, Kind = CamOperationKind.Drill, TotalDepth = 3, DepthPerPass = 1.5, UseTabs = false });
        Add(doc, ShapeKind.Rectangle, 20, 30, 120, 80, new CamOperation { ToolName = "6 mm rak fräs", ToolDiameter = 6, Kind = CamOperationKind.Pocket, TotalDepth = 14, DepthPerPass = 3, UseTabs = false });
        Add(doc, ShapeKind.Ellipse, 150, 120, 8, 8, null);   // not assigned
        return doc;
    }

    [Fact]
    public void Programs_follow_the_tools_in_run_order_and_operations_the_cut_order()
    {
        var doc = Box();
        var tree = CamTree.Build(doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
        Assert.Equal(new[] { "5 mm borr", "6 mm rak fräs" }, tree.Programs.Select(p => p.ToolLabel));
        Assert.Equal(new[] { 1, 2 }, tree.Programs.Select(p => p.Number));
        Assert.Equal(new[] { "Ficka", "Kontur ut" }, tree.Programs[1].Operations.Select(o => o.KindLabel));   // pocket before outside
        Assert.Contains("4 flikar", tree.Programs[1].Operations[1].Detail);
        Assert.Contains("genom", tree.Programs[1].Operations[1].Detail);
        Assert.All(tree.Programs.SelectMany(p => p.Operations), o => Assert.Equal(CamTreeStatus.Ok, o.Status));
        Assert.True(tree.Programs[1].CuttingMinutes > tree.Programs[0].CuttingMinutes);
        var unassigned = Assert.Single(tree.Unassigned);
        Assert.Equal("Ellips 4", unassigned.Label);
    }

    [Fact]
    public void Problems_show_on_the_operation_and_its_program()
    {
        var doc = Box();
        doc.CamOperations[1].ToolDiameter = 3;              // the hole is 5 mm: size warning
        doc.CamOperations[1].ToolName = "3 mm rak fräs";
        doc.CamOperations[2].ToolKind = ToolKind.VBit;      // V-bit pocket: critical
        var tree = CamTree.Build(doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
        var drill = tree.Programs.SelectMany(p => p.Operations).Single(o => o.KindLabel == "Borrning");
        Assert.Equal(CamTreeStatus.Warning, drill.Status);
        var pocket = tree.Programs.SelectMany(p => p.Operations).Single(o => o.KindLabel == "Ficka");
        Assert.Equal(CamTreeStatus.Critical, pocket.Status);
        Assert.Equal(CamTreeStatus.Critical, tree.Programs.Single(p => p.Operations.Contains(pocket)).Status);
    }

    [Fact]
    public void An_empty_project_has_an_empty_tree()
    {
        var doc = new CncDocument();
        var tree = CamTree.Build(doc, new CamPlanner(ContourToolpaths.Generate).Review(doc));
        Assert.Empty(tree.Programs);
        Assert.Empty(tree.Unassigned);
    }
}
