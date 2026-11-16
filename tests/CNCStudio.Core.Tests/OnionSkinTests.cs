using CNCStudio.Core;
using Xunit;

public class OnionSkinTests
{
    private static (CncDocument Doc, CamOperation Op) Project(double skin)
    {
        var doc = new CncDocument { MaterialWidth = 200, MaterialHeight = 100, MaterialThickness = 12, HasMaterial = true };
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 20, Y = 20, Width = 80, Height = 40 };
        doc.Shapes.Add(s);
        var op = new CamOperation { ShapeId = s.Id, Kind = CamOperationKind.Outside, ToolDiameter = 6, TotalDepth = 12, DepthPerPass = 4, UseTabs = true, OnionSkinMm = skin };
        doc.CamOperations.Add(op);
        return (doc, op);
    }

    [Fact]
    public void A_through_cut_stops_the_skin_above_the_bottom_and_has_no_tabs()
    {
        var (doc, _) = Project(0.3);
        var r = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        var op = r.Operations.Single();
        Assert.Equal(new[] { 4.0, 8.0, 11.7 }, op.Preview!.PassDepths);
        Assert.Empty(op.TabSpans);
        Assert.Contains(op.Findings, f => f.Code == "OnionSkin");
        Assert.DoesNotContain(op.Findings, f => f.Code == "LoosePart");
        Assert.True(r.Motion.Moves.Min(m => Math.Min(m.Z0, m.Z1)) >= -11.7 - 1e-9);   // never below the skin
    }

    [Fact]
    public void Without_a_skin_the_cut_goes_through_with_tabs()
    {
        var (doc, _) = Project(0);
        var op = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true).Operations.Single();
        Assert.Equal(12, op.Preview!.PassDepths[^1]);
        Assert.NotEmpty(op.TabSpans);
    }

    [Fact]
    public void A_skin_out_of_range_is_refused()
    {
        var (_, op) = Project(3);
        Assert.NotNull(CamValidation.GetError(op, 12));
    }
}
