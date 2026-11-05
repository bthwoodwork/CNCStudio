using CNCStudio.Core;

namespace CNCStudio.Core.Tests;

public class CamTabsTests
{
    private static (ContourPreview Preview, CamOperation Op) Through(double w = 200, double h = 150, int count = 4, double tabWidth = 6)
    {
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = w, Height = h };
        var op = new CamOperation { ShapeId = shape.Id, TotalDepth = 18, DepthPerPass = 3, UseTabs = true,
            TabCount = count, TabWidth = tabWidth, TabHeight = 3 };
        return (ContourToolpaths.Generate(shape, op, 18), op);
    }

    [Fact]
    public void Requested_tabs_are_placed_evenly_with_tool_diameter_added()
    {
        var (preview, op) = Through();
        var tabs = CamTabs.Plan(preview, op, 18);
        Assert.Equal(4, tabs.Count);
        Assert.All(tabs, t => Assert.Equal(6 + 3.175, t.Length, 9));
        Assert.All(tabs, t => Assert.Equal(15, t.TopDepth, 9)); // 18 mm cut – 3 mm tab
        double perimeter = PathMath.Length(preview.Centerline);
        var centres = tabs.Select(t => (t.Start + t.End) / 2).ToList();
        for (int i = 1; i < centres.Count; i++)
            Assert.Equal(perimeter / 4, centres[i] - centres[i - 1], 6);
    }

    [Fact]
    public void Tabs_lie_within_the_path_and_do_not_overlap()
    {
        var (preview, op) = Through(count: 12);
        var tabs = CamTabs.Plan(preview, op, 18).OrderBy(t => t.Start).ToList();
        double perimeter = PathMath.Length(preview.Centerline);
        Assert.All(tabs, t => Assert.True(t.Start >= 0 && t.End <= perimeter));
        for (int i = 1; i < tabs.Count; i++) Assert.True(tabs[i].Start > tabs[i - 1].End);
    }

    [Fact]
    public void No_tabs_on_partial_depth_cuts()
    {
        var (preview, op) = Through();
        op.TotalDepth = 6;
        Assert.Empty(CamTabs.Plan(preview, op, 18));
    }

    [Fact]
    public void No_tabs_when_switched_off()
    {
        var (preview, op) = Through();
        op.UseTabs = false;
        Assert.Empty(CamTabs.Plan(preview, op, 18));
    }

    [Fact]
    public void Short_path_gets_fewer_tabs()
    {
        var (preview, op) = Through(w: 5, h: 5); // centerline perimeter ≈ 32.7 mm
        Assert.Single(CamTabs.Plan(preview, op, 18));
    }

    [Fact]
    public void Too_short_path_gets_no_tabs_and_a_warning()
    {
        var doc = new CncDocument();
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 1, Height = 1 };
        doc.Shapes.Add(shape);
        doc.CamOperations.Add(new CamOperation { ShapeId = shape.Id, TotalDepth = 18, DepthPerPass = 3, UseTabs = true });
        var result = Assert.Single(new CamPlanner(ContourToolpaths.Generate).Review(doc).Operations);
        Assert.Empty(result.TabSpans);
        Assert.Contains(result.Findings, f => f.Severity == CamSeverity.Warning && f.Code == "NoTabsFit");
    }

    [Fact]
    public void Planner_reports_placed_tabs()
    {
        var doc = new CncDocument();
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 200, Height = 150 };
        doc.Shapes.Add(shape);
        doc.CamOperations.Add(new CamOperation { ShapeId = shape.Id, TotalDepth = 18, DepthPerPass = 3, UseTabs = true });
        var result = Assert.Single(new CamPlanner(ContourToolpaths.Generate).Review(doc).Operations);
        Assert.Equal(4, result.TabSpans.Count);
        Assert.Contains(result.Findings, f => f.Code == "TabsPlaced");
        Assert.DoesNotContain(result.Findings, f => f.Severity <= CamSeverity.Warning);
    }

    [Fact]
    public void Slice_returns_the_requested_length()
    {
        var path = new List<CamPoint> { new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0) };
        var slice = PathMath.Slice(path, 5, 15);
        Assert.Equal(new CamPoint(5, 0), slice[0]);
        Assert.Equal(new CamPoint(10, 0), slice[1]);   // includes the corner
        Assert.Equal(new CamPoint(10, 5), slice[^1]);
        Assert.Equal(10, PathMath.Length(slice), 9);
    }

    [Fact]
    public void Slice_wraps_around_a_closed_path()
    {
        var path = new List<CamPoint> { new(0, 0), new(10, 0), new(10, 10), new(0, 10), new(0, 0) };
        var slice = PathMath.Slice(path, 35, 45);
        Assert.Equal(10, PathMath.Length(slice), 9);
        Assert.Equal(new CamPoint(0, 5), slice[0]);
        Assert.Equal(new CamPoint(5, 0), slice[^1]);
    }
}

public class CamGeometryChecksTests
{
    private static (CncDocument Doc, DesignShape Shape, CamOperation Op) Doc(double x, double y, double w = 100, double h = 50)
    {
        var doc = new CncDocument(); // material 600 × 600 × 18
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, X = x, Y = y, Width = w, Height = h };
        var op = new CamOperation { ShapeId = shape.Id };
        doc.Shapes.Add(shape); doc.CamOperations.Add(op);
        return (doc, shape, op);
    }

    private static IReadOnlyList<CamFinding> Bounds(double x, double y)
    {
        var (doc, shape, op) = Doc(x, y);
        return CamGeometryChecks.MaterialBounds(doc, shape, op, ContourToolpaths.Generate(shape, op, 18));
    }

    [Fact]
    public void Shape_inside_material_gives_no_findings() => Assert.Empty(Bounds(100, 100));

    [Fact]
    public void Shape_partly_outside_material_is_a_warning() =>
        Assert.Contains(Bounds(-10, 100), f => f.Severity == CamSeverity.Warning && f.Code == "ShapeOutsideStock");

    [Fact]
    public void Shape_fully_outside_material_is_a_warning() =>
        Assert.Contains(Bounds(700, 100), f => f.Severity == CamSeverity.Warning && f.Code == "ShapeOffStock");

    [Fact]
    public void Outside_contour_at_the_material_edge_is_info() =>
        Assert.Contains(Bounds(0, 100), f => f.Severity == CamSeverity.Info && f.Code == "PathBeyondStock");

    private static CamReport TwoParts(double gap, bool secondHasCam = true)
    {
        var doc = new CncDocument();
        var a = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 100, Height = 50 };
        var b = new DesignShape { Kind = ShapeKind.Rectangle, X = 200 + gap, Y = 100, Width = 100, Height = 50 };
        doc.Shapes.Add(a); doc.Shapes.Add(b);
        doc.CamOperations.Add(new CamOperation { ShapeId = a.Id });
        if (secondHasCam) doc.CamOperations.Add(new CamOperation { ShapeId = b.Id });
        return new CamPlanner(ContourToolpaths.Generate).Review(doc);
    }

    [Fact]
    public void Parts_far_enough_apart_do_not_conflict() =>
        Assert.DoesNotContain(TwoParts(10).AllFindings, f => f.Code == "CutsOtherPart");

    [Fact]
    public void Path_cutting_into_a_neighbouring_part_is_a_warning()
    {
        // 1 mm gap, Ø 3.175: each outside path reaches 3.175 mm beyond its own part.
        var findings = TwoParts(1).AllFindings.Where(f => f.Code == "CutsOtherPart").ToList();
        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal(CamSeverity.Warning, f.Severity));
        Assert.Contains(findings, f => f.Message.Contains("Rektangel 2"));
        Assert.Contains(findings, f => f.Message.Contains("Rektangel 1"));
    }

    [Fact]
    public void Shapes_without_cam_are_not_treated_as_parts() =>
        Assert.DoesNotContain(TwoParts(1, secondHasCam: false).AllFindings, f => f.Code == "CutsOtherPart");

    [Fact]
    public void Band_entirely_inside_another_region_is_detected()
    {
        var square = new[] { (IReadOnlyList<CamPoint>)new[] { new CamPoint(0, 0), new CamPoint(100, 0), new CamPoint(100, 100), new CamPoint(0, 100) } };
        Assert.True(CamGeometryChecks.BandHitsRegion(new CamPoint(40, 50), new CamPoint(60, 50), 1, square));
        Assert.False(CamGeometryChecks.BandHitsRegion(new CamPoint(140, 50), new CamPoint(160, 50), 1, square));
        Assert.True(CamGeometryChecks.BandHitsRegion(new CamPoint(101, 50), new CamPoint(120, 50), 1.5, square));
    }

    [Fact]
    public void Hole_in_a_vector_part_is_not_part_of_it()
    {
        // Square with a square hole (even-odd); a band in the hole touches nothing.
        var shape = new DesignShape { Kind = ShapeKind.VectorPath, X = 0, Y = 0, Width = 100, Height = 100,
            Contours = {
                new VectorContour { Points = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) } },
                new VectorContour { Points = { new(.2, .2), new(.8, .2), new(.8, .8), new(.2, .8) } } } };
        var outline = CamGeometryChecks.Outline(shape);
        Assert.False(CamGeometryChecks.BandHitsRegion(new CamPoint(45, 50), new CamPoint(55, 50), 2, outline));
        Assert.True(CamGeometryChecks.BandHitsRegion(new CamPoint(5, 50), new CamPoint(10, 50), 2, outline));
    }

    [Fact]
    public void Ellipse_outline_encloses_the_true_ellipse()
    {
        var e = new DesignShape { Kind = ShapeKind.Ellipse, X = 0, Y = 0, Width = 100, Height = 60 };
        var outline = CamGeometryChecks.Outline(e);
        for (int i = 0; i < 360; i++)
        {
            double t = i * Math.PI / 180;
            var onEllipse = new CamPoint(50 + 49.99 * Math.Cos(t), 30 + 29.99 * Math.Sin(t));
            Assert.True(CamGeometryChecks.Inside(onEllipse, outline), $"angle {i}");
        }
    }
}
