using CNCStudio.Core;
using Xunit;

public class ChamferTests
{
    [Fact]
    public void The_depth_gives_the_width_for_the_bit_angle()
    {
        Assert.Equal(2, ChamferToolpaths.Depth(2, 90), 6);                 // 45° chamfer: as deep as wide
        Assert.Equal(2 / Math.Tan(Math.PI / 6), ChamferToolpaths.Depth(2, 60), 6);
        Assert.Null(ChamferToolpaths.Validate(3, 12.7, 90));
        Assert.NotNull(ChamferToolpaths.Validate(7, 12.7, 90));              // wider than the flank
    }

    [Fact]
    public void A_contour_with_a_chamfer_cuts_the_chamfer_first_with_the_V_bit_along_the_edge()
    {
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, X = 10, Y = 10, Width = 100, Height = 50 };
        var op = new CamOperation { ShapeId = shape.Id, Kind = CamOperationKind.Outside, ToolDiameter = 6, TotalDepth = 18, DepthPerPass = 3,
            ChamferEnabled = true, ChamferWidthMm = 2, ChamferToolDiameter = 12.7, ChamferAngleDeg = 90, ChamferToolName = "V 90°" };
        var passes = CamSecondPass.Expand(new[] { op }).ToList();
        Assert.Equal(CamOperationKind.Chamfer, passes[0].Kind);
        Assert.Same(op, passes[1]);
        var chamfer = passes[0];
        Assert.Equal(ToolKind.VBit, chamfer.ToolKind);
        Assert.Equal(2, chamfer.TotalDepth, 3);
        Assert.Null(CamValidation.GetError(chamfer, 18));

        var preview = ContourToolpaths.Generate(shape, chamfer, 18);
        Assert.Equal(new CamPoint(10, 10), preview.Centerline[0]);           // the tip runs on the edge itself
        Assert.Equal(new[] { 2.0 }, preview.PassDepths);
    }

    [Fact]
    public void A_planned_project_puts_the_chamfer_in_its_own_program_before_the_cut()
    {
        var doc = new CncDocument { MaterialWidth = 200, MaterialHeight = 100, MaterialThickness = 18, HasMaterial = true };
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, X = 20, Y = 20, Width = 100, Height = 50 };
        doc.Shapes.Add(shape);
        doc.CamOperations.Add(new CamOperation { ShapeId = shape.Id, Kind = CamOperationKind.Outside, ToolDiameter = 6, TotalDepth = 18, DepthPerPass = 3, UseTabs = true,
            ChamferEnabled = true, ChamferWidthMm = 1.5, ChamferToolDiameter = 12.7, ChamferAngleDeg = 90, ChamferToolName = "V 90°" });
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        Assert.DoesNotContain(report.AllFindings, f => f.Severity == CamSeverity.Critical);
        var tree = CamTree.Build(doc, report);
        Assert.Equal(2, tree.Programs.Count);
        Assert.Equal("Fasning", tree.Programs[0].Operations[0].KindLabel);
    }
}
