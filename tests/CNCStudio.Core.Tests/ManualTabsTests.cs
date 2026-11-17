using CNCStudio.Core;
using Xunit;

public class ManualTabsTests
{
    private static (DesignShape Shape, CamOperation Op, ContourPreview Preview) Cut()
    {
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, X = 10, Y = 10, Width = 100, Height = 50 };
        var op = new CamOperation { ShapeId = shape.Id, Kind = CamOperationKind.Outside, ToolDiameter = 6, TotalDepth = 18, DepthPerPass = 3, UseTabs = true, TabCount = 4, TabWidth = 6 };
        return (shape, op, ContourToolpaths.Generate(shape, op, 18));
    }

    [Fact]
    public void A_hand_placed_tab_lands_on_the_nearest_point_of_the_path()
    {
        var (shape, op, preview) = Cut();
        op.ManualTabs = new() { new TabPoint(50, -2) };            // just above the top edge, in the middle
        var tab = Assert.Single(CamTabs.Plan(preview, op, 18, shape));
        var centre = PathMath.PointAt(preview.Centerline, (tab.Start + tab.End) / 2);
        Assert.Equal(60, centre.X, 3);                             // shape.X + 50
        Assert.Equal(7, centre.Y, 3);                              // the path runs 3 mm outside the top edge
    }

    [Fact]
    public void Overlapping_hand_placed_tabs_keep_only_the_first_and_none_means_spread_evenly()
    {
        var (shape, op, preview) = Cut();
        op.ManualTabs = new() { new TabPoint(50, 0), new TabPoint(52, 0), new TabPoint(0, 25) };
        Assert.Equal(2, CamTabs.Plan(preview, op, 18, shape).Count);
        op.ManualTabs = null;
        Assert.Equal(4, CamTabs.Plan(preview, op, 18, shape).Count);
    }

    [Fact]
    public void The_centres_of_the_even_tabs_become_the_same_tabs_when_placed_by_hand()
    {
        var (shape, op, preview) = Cut();
        var even = CamTabs.Plan(preview, op, 18, shape);
        op.ManualTabs = CamTabs.Centres(preview, even, shape);
        var manual = CamTabs.Plan(preview, op, 18, shape);
        Assert.Equal(even.Select(t => Math.Round(t.Start, 2)), manual.Select(t => Math.Round(t.Start, 2)));
    }
}
