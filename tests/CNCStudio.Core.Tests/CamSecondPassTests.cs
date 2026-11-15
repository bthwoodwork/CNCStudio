using Clipper2Lib;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

public class CamSecondPassTests
{
    private static CncDocument Doc(DesignShape shape, CamOperation op)
    {
        var doc = new CncDocument { Name = "Två fräsar" };
        doc.Motion.StockTopMachineZ = -40;
        doc.Shapes.Add(shape);
        doc.CamOperations.Add(op);
        return doc;
    }

    private static CamOperation Pocket(Guid id, double big, double small) => new()
    {
        ShapeId = id, Kind = CamOperationKind.Pocket, ToolName = $"{big} mm", ToolDiameter = big, TotalDepth = 3, DepthPerPass = 3, UseTabs = false,
        SecondToolEnabled = true, SecondToolName = $"{small} mm", SecondToolDiameter = small, SecondDepthPerPass = 3,
    };

    [Fact]
    public void Expand_puts_details_after_the_pocket_and_bottoms_before_the_v_carving()
    {
        var p = Pocket(Guid.NewGuid(), 10, 3);
        var v = new CamOperation { Kind = CamOperationKind.VCarve, ToolKind = ToolKind.VBit, ToolDiameter = 12.7, TotalDepth = 3, SecondToolEnabled = true, SecondToolDiameter = 6 };
        var list = CamSecondPass.Expand(new[] { p, v }).ToList();
        Assert.Equal(new[] { CamOperationKind.Pocket, CamOperationKind.PocketRest, CamOperationKind.VCarveClear, CamOperationKind.VCarve }, list.Select(o => o.Kind));
        Assert.Same(p, list[1].Parent);
        Assert.Equal(3, list[1].ToolDiameter);
    }

    [Fact]
    public void Rest_machining_cuts_the_narrow_slot_the_big_cutter_cannot_enter()
    {
        // A 60 × 40 pocket with a 6 mm wide, 30 mm long slot off its right side.
        var shape = new DesignShape { Kind = ShapeKind.VectorPath, X = 10, Y = 10, Width = 90, Height = 40,
            Contours = { new VectorContour { Points = { new(0, 0), new(60 / 90.0, 0), new(60 / 90.0, 17 / 40.0), new(1, 17 / 40.0), new(1, 23 / 40.0), new(60 / 90.0, 23 / 40.0), new(60 / 90.0, 1), new(0, 1) } } } };
        var op = Pocket(shape.Id, 10, 3);
        var rest = CamSecondPass.Generate(shape, CamSecondPass.For(op)!, 10);
        Assert.True(rest.IsPocket);
        // The small cutter goes into the slot (x 70–100) – where the big one never could.
        Assert.Contains(rest.Centerlines.SelectMany(l => l), q => q.X > 85 && q.Y > 26 && q.Y < 34);
        // But not round the middle of the big pocket.
        Assert.DoesNotContain(rest.Centerlines.SelectMany(l => l), q => q.X > 25 && q.X < 55 && q.Y > 20 && q.Y < 40);
    }

    [Fact]
    public void Rest_is_refused_when_the_big_cutter_reaches_everything()
    {
        var circle = new DesignShape { Kind = ShapeKind.Ellipse, X = 0, Y = 0, Width = 40, Height = 40 };
        var ex = Assert.Throws<ArgumentException>(() => CamSecondPass.Generate(circle, CamSecondPass.For(Pocket(circle.Id, 6, 3))!, 10));
        Assert.Contains("behövs inte", ex.Message);
        Assert.NotNull(CamValidation.GetError(Pocket(circle.Id, 3, 6), 10));     // the detail cutter must be smaller
    }

    [Fact]
    public void Straight_cutter_clears_the_v_carvings_bottom_and_the_v_bit_does_less()
    {
        var rect = new DesignShape { Kind = ShapeKind.Rectangle, X = 20, Y = 20, Width = 60, Height = 40 };
        CamOperation V(bool second) => new() { ShapeId = rect.Id, Kind = CamOperationKind.VCarve, ToolKind = ToolKind.VBit, ToolAngleDeg = 60, ToolDiameter = 12.7,
            TotalDepth = 3, DepthPerPass = 3, UseTabs = false, SecondToolEnabled = second, SecondToolDiameter = 6, SecondDepthPerPass = 3 };
        int alone = VCarveToolpaths.Generate(rect, V(false), 10).Centerlines.Count;
        var withFlat = V(true);
        int vLoops = VCarveToolpaths.Generate(rect, withFlat, 10).Centerlines.Count;
        Assert.True(vLoops < alone / 2, $"{vLoops} vs {alone}");
        var clear = CamSecondPass.Generate(rect, CamSecondPass.For(withFlat)!, 10);
        Assert.Equal(new[] { 3.0 }, clear.PassDepths);
        // The straight cutter stays inside the full-depth bottom (3 · tan 30° ≈ 1.73 mm in) plus its radius.
        double inset = 3 * Math.Tan(Math.PI / 6) + 3 - 0.05;
        Assert.All(clear.Centerlines.SelectMany(l => l), q => Assert.True(q.X >= 20 + inset && q.X <= 80 - inset && q.Y >= 20 + inset && q.Y <= 60 - inset, q.ToString()));
    }

    [Fact]
    public void Two_tools_give_two_programs()
    {
        var rect = new DesignShape { Kind = ShapeKind.Rectangle, X = 20, Y = 20, Width = 60, Height = 40 };
        var doc = Doc(rect, new CamOperation { ShapeId = rect.Id, Kind = CamOperationKind.VCarve, ToolKind = ToolKind.VBit, ToolName = "60° V", ToolAngleDeg = 60, ToolDiameter = 12.7,
            TotalDepth = 3, DepthPerPass = 3, UseTabs = false, SecondToolEnabled = true, SecondToolName = "6 mm rak", SecondToolDiameter = 6, SecondDepthPerPass = 3 });
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        Assert.Equal(2, report.Operations.Count);
        Assert.All(report.Operations, r => Assert.True(r.Analyzed, string.Join("; ", r.Findings.Select(f => f.Message))));
        Assert.DoesNotContain(report.Operations.SelectMany(r => r.Findings), f => f.Severity == CamSeverity.Critical);
        var (jobs, blocking) = GCodeGenerator.GenerateByTool(doc, report);
        Assert.Empty(blocking);
        Assert.Equal(2, jobs.Count);
        Assert.Equal("6 mm rak", jobs[0].ToolLabel);                     // the bottoms first
        Assert.All(jobs, j => Assert.True(j.Result.Success, string.Join("; ", j.Result.BlockingReasons)));
        Assert.Single(doc.CamOperations);                                  // the second pass is never stored
    }
}
