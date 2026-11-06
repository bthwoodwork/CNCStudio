namespace CNCStudio.Core.Tests;

public class StockSimulatorTests
{
    private static (CncDocument Doc, CamReport Report) Plan(CamOperation op, DesignShape? shape = null, double thickness = 18)
    {
        var doc = new CncDocument { MaterialWidth = 200, MaterialHeight = 150, MaterialThickness = thickness };
        var s = shape ?? new DesignShape { Kind = ShapeKind.Rectangle, X = 50, Y = 40, Width = 80, Height = 60 };
        op.ShapeId = s.Id; doc.Shapes.Add(s); doc.CamOperations.Add(op);
        return (doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
    }

    private static double HeightAt(StockHeightMap m, double x, double y) =>
        m[(int)(x / m.Cell), (int)(y / m.Cell)];

    [Fact]
    public void Untouched_material_stays_at_the_surface()
    {
        var (doc, report) = Plan(new CamOperation { TotalDepth = 6, DepthPerPass = 2 });
        var map = StockSimulator.Simulate(doc, report, 0.5);
        Assert.Equal(0, HeightAt(map, 5, 5));      // corner far from the cut
        Assert.Equal(0, HeightAt(map, 90, 80));    // inside the part (design Y 40..100 → work Y 50..110)
    }

    [Fact]
    public void Contour_groove_reaches_the_programmed_depth()
    {
        // Outside contour of x 50..130: the tool centre runs at x 48.41, groove 46.8..50.
        var (doc, report) = Plan(new CamOperation { TotalDepth = 6, DepthPerPass = 2 });
        var map = StockSimulator.Simulate(doc, report, 0.25);
        Assert.Equal(-6, HeightAt(map, 48.4, 80), 3);
        Assert.Equal(-6, map.MinHeight, 3);
    }

    [Fact]
    public void Pocket_floor_is_cleared_to_depth_everywhere_reachable()
    {
        var (doc, report) = Plan(new CamOperation { Kind = CamOperationKind.Pocket, UseTabs = false, ToolDiameter = 6,
            TotalDepth = 4, DepthPerPass = 2 });
        var map = StockSimulator.Simulate(doc, report, 0.5);
        // Pocket x 50..130, work y 50..110: sample a grid well inside it.
        for (double x = 56; x <= 124; x += 4)
            for (double y = 56; y <= 104; y += 4)
                Assert.Equal(-4, HeightAt(map, x, y), 3);
        Assert.Equal(0, HeightAt(map, 20, 20)); // outside stays
    }

    [Fact]
    public void Through_cut_is_marked_as_through_and_tabs_remain()
    {
        var (doc, report) = Plan(new CamOperation { TotalDepth = 18, DepthPerPass = 3, UseTabs = true, TabHeight = 3 });
        var map = StockSimulator.Simulate(doc, report, 0.25);
        Assert.Equal(-18, map.MinHeight, 3);
        int through = 0, tabCells = 0;
        for (int iy = 0; iy < map.Ny; iy++)
            for (int ix = 0; ix < map.Nx; ix++)
            {
                if (map.IsThrough(ix, iy)) through++;
                if (Math.Abs(map[ix, iy] + 15) < 0.01) tabCells++; // tab top at −15
            }
        Assert.True(through > 0);
        Assert.True(tabCells > 0, "tabs should be left at −15 mm");
    }

    [Fact]
    public void Ball_nose_leaves_a_rounded_groove()
    {
        var (doc, report) = Plan(new CamOperation { ToolKind = ToolKind.BallNose, ToolDiameter = 6, TotalDepth = 3, DepthPerPass = 3 });
        var map = StockSimulator.Simulate(doc, report, 0.25);
        // Centre line of the left groove at x = 47: full depth; 2 mm to the side the ball is shallower.
        double centre = HeightAt(map, 47.0, 80), side = HeightAt(map, 45.0, 80);
        Assert.Equal(-3, centre, 1);
        Assert.True(side > centre + 0.4, $"side {side} should be shallower than centre {centre}");
    }

    [Fact]
    public void Removed_volume_matches_a_simple_groove()
    {
        // 6 mm deep, 3.175 wide groove around the part: volume ≈ perimeter × width × depth.
        var (doc, report) = Plan(new CamOperation { TotalDepth = 6, DepthPerPass = 6 });
        var map = StockSimulator.Simulate(doc, report, 0.2);
        double perimeter = 2 * (83.175 + 63.175);
        double expected = perimeter * 3.175 * 6;
        Assert.InRange(map.RemovedVolume, expected * 0.9, expected * 1.1);
    }

    [Fact]
    public void Big_sheets_get_a_coarser_grid()
    {
        Assert.Equal(0.25, StockSimulator.CellSizeFor(100, 100));
        double cell = StockSimulator.CellSizeFor(1200, 2400);
        Assert.True(1200 / cell * (2400 / cell) <= StockSimulator.MaxCells * 1.01);
    }
}
