using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

public class PocketTests
{
    private static CamOperation Pocket(double d = 6, double step = 40, double depth = 6) =>
        new() { Kind = CamOperationKind.Pocket, UseTabs = false, ToolDiameter = d, StepoverPercent = step,
                TotalDepth = depth, DepthPerPass = 2 };

    private static DesignShape Rect(double x = 100, double y = 100, double w = 100, double h = 60) =>
        new() { Kind = ShapeKind.Rectangle, X = x, Y = y, Width = w, Height = h };

    /// <summary>100 × 100 square with a 40 × 40 square hole in the middle (an island in the pocket).</summary>
    private static DesignShape Frame() => new()
    {
        Kind = ShapeKind.VectorPath, X = 100, Y = 100, Width = 100, Height = 100,
        Contours =
        {
            new VectorContour { Points = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) } },
            new VectorContour { Points = { new(0.3, 0.3), new(0.7, 0.3), new(0.7, 0.7), new(0.3, 0.7) } }
        }
    };

    [Fact]
    public void Rectangle_pocket_ends_with_a_finish_loop_one_radius_from_the_wall()
    {
        var p = PocketToolpaths.Generate(Rect(), Pocket(), 18);
        Assert.True(p.IsPocket);
        Assert.True(p.Centerlines.Count > 3);
        var last = p.Centerlines[^1];
        Assert.Equal(103, last.Min(v => v.X), 1);
        Assert.Equal(197, last.Max(v => v.X), 1);
        Assert.Equal(103, last.Min(v => v.Y), 1);
        Assert.Equal(157, last.Max(v => v.Y), 1);
    }

    [Fact]
    public void Every_loop_stays_inside_the_wall_minus_the_tool_radius()
    {
        var p = PocketToolpaths.Generate(Rect(), Pocket(), 18);
        Assert.All(p.Centerlines.SelectMany(l => l), v =>
        {
            Assert.InRange(v.X, 103 - 0.02, 197 + 0.02);
            Assert.InRange(v.Y, 103 - 0.02, 157 + 0.02);
        });
    }

    [Fact]
    public void Loops_are_cut_from_the_inside_out()
    {
        var p = PocketToolpaths.Generate(Rect(), Pocket(), 18);
        double Area(IReadOnlyList<CamPoint> l) => (l.Max(v => v.X) - l.Min(v => v.X)) * (l.Max(v => v.Y) - l.Min(v => v.Y));
        for (int i = 1; i < p.Centerlines.Count; i++)
            Assert.True(Area(p.Centerlines[i]) >= Area(p.Centerlines[i - 1]) - 1e-6);
    }

    [Fact]
    public void Normal_stepover_leaves_no_material_between_loops()
    {
        Assert.True(PocketToolpaths.Generate(Rect(), Pocket(step: 40), 18).PocketRestArea < 0.5);
    }

    [Fact]
    public void Too_large_stepover_is_reported_as_rest_material()
    {
        var doc = new CncDocument();
        var s = Rect(); doc.Shapes.Add(s);
        var op = Pocket(step: 90); op.ShapeId = s.Id; doc.CamOperations.Add(op);
        var result = Assert.Single(new CamPlanner(ContourToolpaths.Generate).Review(doc).Operations);
        Assert.Contains(result.Findings, f => f.Severity == CamSeverity.Warning && f.Code == "PocketRest");
    }

    [Fact]
    public void Rectangle_corners_keep_the_tool_radius_and_ellipses_do_not()
    {
        // Four corners of radius 3: 4 × (r² − πr²/4) ≈ 7.73 mm².
        Assert.Equal(7.73, PocketToolpaths.Generate(Rect(), Pocket(), 18).PocketCornerArea, 0);
        var ellipse = new DesignShape { Kind = ShapeKind.Ellipse, X = 0, Y = 0, Width = 120, Height = 80 };
        Assert.True(PocketToolpaths.Generate(ellipse, Pocket(), 18).PocketCornerArea < 0.5);
    }

    [Fact]
    public void Tool_larger_than_the_pocket_is_refused()
    {
        Assert.Throws<ArgumentException>(() => PocketToolpaths.Generate(Rect(w: 5, h: 5), Pocket(d: 6), 18));
    }

    [Fact]
    public void Island_is_never_entered()
    {
        var p = PocketToolpaths.Generate(Frame(), Pocket(), 18);
        // Island 130..170: the tool centre must stay at least the radius (3 mm) from it. At the
        // island's corners that zone is rounded, so measure the true distance to the square.
        static double DistanceToIsland(CamPoint v)
        {
            double dx = Math.Max(0, Math.Max(130 - v.X, v.X - 170)), dy = Math.Max(0, Math.Max(130 - v.Y, v.Y - 170));
            return Math.Sqrt(dx * dx + dy * dy);
        }
        Assert.All(p.Centerlines.SelectMany(l => l), v =>
            Assert.True(DistanceToIsland(v) >= 3 - 0.02, $"({v.X}, {v.Y}) is {DistanceToIsland(v):0.###} mm from the island"));
    }

    [Fact]
    public void Pocket_links_never_cross_the_island_or_the_wall()
    {
        var doc = new CncDocument();
        var s = Frame(); doc.Shapes.Add(s);
        var op = Pocket(); op.ShapeId = s.Id; doc.CamOperations.Add(op);
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc);
        var region = report.Operations[0].Preview!.PocketCenterRegion;
        var links = report.Motion.Moves.Where(m => m.Purpose == "Länk inom fickan").ToList();
        Assert.All(links, m => Assert.True(PocketToolpaths.SegmentInside(new CamPoint(m.X0, m.Y0), new CamPoint(m.X1, m.Y1), region)));
        Assert.All(links, m => Assert.False(m.X0 is > 127 and < 173 && m.X1 is > 127 and < 173 &&
            ((m.Y0 < 127 && m.Y1 > 173) || (m.Y1 < 127 && m.Y0 > 173)), "link crosses the island"));
    }

    [Fact]
    public void Pocket_is_cleared_level_by_level()
    {
        var doc = new CncDocument();
        var s = Rect(); doc.Shapes.Add(s);
        var op = Pocket(depth: 6); op.ShapeId = s.Id; doc.CamOperations.Add(op);
        var cuts = new CamPlanner(ContourToolpaths.Generate).Review(doc).Motion.Moves.Where(m => m.FollowsPath).ToList();
        var depths = cuts.Select(m => m.Z0).ToList();
        // All cuts at −2 come before any at −4, which come before any at −6.
        Assert.Equal(depths.OrderByDescending(d => d), depths);
        Assert.Equal(new[] { -2.0, -4.0, -6.0 }, depths.Distinct());
    }

    [Fact]
    public void Pocket_links_feed_inside_instead_of_retracting()
    {
        var doc = new CncDocument();
        var s = Rect(); doc.Shapes.Add(s);
        var op = Pocket(); op.ShapeId = s.Id; doc.CamOperations.Add(op);
        var moves = new CamPlanner(ContourToolpaths.Generate).Review(doc).Motion.Moves;
        Assert.Contains(moves, m => m.Purpose == "Länk inom fickan");
        // One plunge per depth level only (no lifts between loops in a plain rectangle).
        Assert.Equal(3, moves.Count(m => m.Purpose.StartsWith("Nedmatning")));
    }

    [Fact]
    public void Pockets_are_cut_before_outside_contours()
    {
        var doc = new CncDocument();
        var outer = Rect(50, 50, 300, 300); var pocket = Rect(100, 100, 80, 80);
        doc.Shapes.Add(outer); doc.Shapes.Add(pocket);
        doc.CamOperations.Add(new CamOperation { ShapeId = outer.Id, Kind = CamOperationKind.Outside });
        var op = Pocket(); op.ShapeId = pocket.Id; doc.CamOperations.Add(op);
        Assert.Equal(new[] { 1, 0 }, new CamPlanner(ContourToolpaths.Generate).Review(doc).Motion.OperationOrder);
    }

    [Fact]
    public void Moving_a_pocket_shifts_its_centre_region_too()
    {
        var doc = new CncDocument();
        var s = Rect(); doc.Shapes.Add(s);
        var op = Pocket(); op.ShapeId = s.Id; doc.CamOperations.Add(op);
        var planner = new CamPlanner(ContourToolpaths.Generate);
        var before = planner.Review(doc).Operations[0].Preview!;
        s.X += 50;
        var after = planner.Review(doc).Operations[0].Preview!;
        Assert.Equal(1, planner.GenerationCount);
        Assert.True(after.IsPocket);
        Assert.Equal(before.PocketCenterRegion[0].Min(v => v.X) + 50, after.PocketCenterRegion[0].Min(v => v.X), 6);
        Assert.Equal(before.PocketRestArea, after.PocketRestArea);
    }

    [Fact]
    public void Pocket_g_code_is_generated_and_verified()
    {
        var doc = new CncDocument();
        doc.Motion.StockTopMachineZ = -40;
        var s = Frame(); doc.Shapes.Add(s);
        var op = Pocket(); op.ShapeId = s.Id; doc.CamOperations.Add(op);
        var r = GCodeGenerator.Generate(doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
        Assert.True(r.Success, string.Join("\n", r.BlockingReasons.Concat(r.VerifierIssues)));
        Assert.Contains(r.Lines, l => l.StartsWith("G1 X") && l.EndsWith("F1000"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(91)]
    public void Stepover_outside_5_to_90_percent_is_refused(double step)
    {
        Assert.NotNull(CamValidation.GetError(Pocket(step: step), 18));
    }
}
