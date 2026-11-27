using System.Numerics;

namespace CNCStudio.Core.Tests;

public class FinishStepoverTests
{
    private static DesignShape Pyramid()
    {
        var a = new Vector3(0, 0, 0); var b = new Vector3(100, 0, 0); var c = new Vector3(100, 100, 0); var d = new Vector3(0, 100, 0); var top = new Vector3(50, 50, 20);
        var stl = Stl.Write(new[] { (a, b, top), (b, c, top), (c, d, top), (d, a, top), (a, c, b), (a, d, c) });
        return new DesignShape { Kind = ShapeKind.Rectangle, X = 10, Y = 10, Width = 40, Height = 40, Model = Model3D.FromStl(stl, "pyramid", 6) };
    }

    private static CamOperation Op(DesignShape s, double? stepMm) => new()
    {
        ShapeId = s.Id, Kind = CamOperationKind.Carve3D, ToolKind = ToolKind.FlatEndMill, ToolDiameter = 6, TotalDepth = 6, DepthPerPass = 3,
        StepoverPercent = 40, UseTabs = false, SecondToolEnabled = true, SecondToolKind = ToolKind.TaperedBallNose,
        SecondToolDiameter = 0.8, SecondToolAngleDeg = 11, SecondDepthPerPass = 6, SecondStepoverMm = stepMm,
    };

    [Fact]
    public void Without_a_value_the_finishing_keeps_ten_percent_of_the_tool()
    {
        var s = Pyramid();
        var op = Op(s, null);
        Assert.Equal(0.08, CamSecondPass.FinishStepoverMm(op), 9);
        Assert.Equal(10, CamSecondPass.For(op)!.StepoverPercent, 9);
    }

    [Fact]
    public void A_set_stepover_in_mm_spaces_the_rows()
    {
        var s = Pyramid();
        var fine = Carve3DToolpaths.Generate(s, CamSecondPass.For(Op(s, 0.1))!, 18);
        var coarse = Carve3DToolpaths.Generate(s, CamSecondPass.For(Op(s, 0.2))!, 18);
        Assert.Equal(25, CamSecondPass.For(Op(s, 0.2))!.StepoverPercent, 9);
        // Distinct rows (Y values) of the zig-zag: half as many with twice the step.
        int Rows(ContourPreview p) => p.Centerlines.SelectMany(c => c).Select(q => Math.Round(q.Y, 3)).Distinct().Count();
        Assert.InRange((double)Rows(fine) / Rows(coarse), 1.7, 2.3);
        // A different stepover is a different toolpath.
        Assert.NotEqual(CamPlanner.Fingerprint(s, CamSecondPass.For(Op(s, 0.1))!, 18), CamPlanner.Fingerprint(s, CamSecondPass.For(Op(s, 0.2))!, 18));
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.75)]
    public void Stepover_must_be_reasonable(double mm)
    {
        var s = Pyramid();
        Assert.NotNull(CamValidation.GetError(Op(s, mm), 18));
        Assert.Null(CamValidation.GetError(Op(s, 0.2), 18));
    }

    [Fact]
    public void Finishing_is_not_warned_for_a_depth_per_pass_it_never_uses()
    {
        var s = Pyramid();
        var op = Op(s, 0.2);
        var finish = CamSecondPass.For(op)!;
        Assert.True(finish.DepthPerPass > finish.ToolDiameter);
        Assert.DoesNotContain(CamOperationChecks.Analyze(finish, s, 18), f => f.Code == "DeepPass");
        // Roughing still is.
        op.DepthPerPass = 8; op.TotalDepth = 6;
        Assert.Contains(CamOperationChecks.Analyze(op, s, 18), f => f.Code == "DeepPass");
    }
}
