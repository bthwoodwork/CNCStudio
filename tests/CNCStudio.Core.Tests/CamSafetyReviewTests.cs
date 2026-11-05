using CNCStudio.Core;

namespace CNCStudio.Core.Tests;

public class CamSafetyReviewTests
{
    // Default document: machine 750 × 750, stock at (50, 50).
    // Rectangle (100,100) 200 × 150 stock-local, outside contour Ø 3.175:
    // the path's left edge lies at machine X = 50 + 100 - 1.5875 = 148.4125.
    private const double PathLeftX = 148.4125;
    private const double ToolRadius = 3.175 / 2;

    private static (CncDocument Doc, DesignShape Shape, CamOperation Op, ContourPreview Preview) Setup(params FixtureBlock[] fixtures)
    {
        var doc = new CncDocument();
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 200, Height = 150 };
        var op = new CamOperation { ShapeId = shape.Id };
        doc.Shapes.Add(shape); doc.CamOperations.Add(op); doc.Fixtures.AddRange(fixtures);
        return (doc, shape, op, ContourToolpaths.Generate(shape, op, doc.MaterialThickness));
    }

    [Fact]
    public void Fixture_over_the_path_is_critical()
    {
        var (doc, shape, op, preview) = Setup(new FixtureBlock { X = 140, Y = 140, Width = 30, Length = 30, ClearanceMm = 5 });
        var findings = CamSafetyReview.Analyze(doc, shape, op, preview);
        Assert.Contains(findings, f => f.Severity == CamSeverity.Critical && f.Code == "FixtureCollision");
    }

    [Fact]
    public void Fixture_only_within_clearance_is_a_warning_not_critical()
    {
        // Fixture's right edge 2 mm from the tool's edge, clearance 5 mm.
        double right = PathLeftX - ToolRadius - 2;
        var (doc, shape, op, preview) = Setup(new FixtureBlock { X = right - 30, Y = 200, Width = 30, Length = 30, ClearanceMm = 5 });
        var findings = CamSafetyReview.Analyze(doc, shape, op, preview);
        Assert.Contains(findings, f => f.Severity == CamSeverity.Warning && f.Code == "FixtureClearance");
        Assert.DoesNotContain(findings, f => f.Severity == CamSeverity.Critical);
    }

    [Fact]
    public void Distant_fixture_gives_no_findings()
    {
        var (doc, shape, op, preview) = Setup(new FixtureBlock { X = 600, Y = 600, Width = 30, Length = 30 });
        Assert.Empty(CamSafetyReview.Analyze(doc, shape, op, preview));
    }

    [Fact]
    public void Conflict_marks_only_the_affected_part_of_a_segment()
    {
        // Small fixture beside the middle of the left edge (machine Y 200..230 on a 148..302 edge).
        var (doc, shape, op, preview) = Setup(new FixtureBlock { X = PathLeftX - 10, Y = 200, Width = 10, Length = 30, ClearanceMm = 0 });
        var critical = Assert.Single(CamSafetyReview.Analyze(doc, shape, op, preview),
            f => f.Severity == CamSeverity.Critical);
        Assert.True(critical.TStart > 0.1 && critical.TEnd < 0.9,
            $"Expected a partial interval, got {critical.TStart}..{critical.TEnd}");
    }

    [Fact]
    public void Collision_with_clearance_band_splits_into_critical_and_warning_parts()
    {
        var (doc, shape, op, preview) = Setup(new FixtureBlock { X = PathLeftX - 10, Y = 200, Width = 10, Length = 30, ClearanceMm = 5 });
        var onLeftEdge = CamSafetyReview.Analyze(doc, shape, op, preview)
            .Where(f => f.SegmentIndex == 3).ToList(); // left edge runs from bottom-left to top-left
        var core = Assert.Single(onLeftEdge, f => f.Severity == CamSeverity.Critical);
        var bands = onLeftEdge.Where(f => f.Severity == CamSeverity.Warning).ToList();
        Assert.Equal(2, bands.Count);
        Assert.All(bands, b => Assert.True(b.TEnd <= core.TStart + 1e-9 || b.TStart >= core.TEnd - 1e-9));
    }

    [Fact]
    public void Path_outside_machine_is_critical()
    {
        var (doc, shape, op, _) = Setup();
        doc.Stock.X = 0; shape.X = 0;
        var preview = ContourToolpaths.Generate(shape, op, doc.MaterialThickness);
        Assert.Contains(CamSafetyReview.Analyze(doc, shape, op, preview),
            f => f.Severity == CamSeverity.Critical && f.Code == "OutsideMachine");
    }

    [Theory]
    [InlineData(0, 25, 100, 25, 40, 0, 60, 50, true, 0.4, 0.6)]   // passes through
    [InlineData(10, 10, 10, 160, 0, 0, 60, 50, true, 0, 0.2667)]  // starts inside
    [InlineData(0, 80, 100, 80, 40, 0, 60, 50, false, 0, 0)]      // misses
    public void SegmentRectInterval_returns_the_inside_portion(double x0, double y0, double x1, double y1,
        double l, double t, double r, double b, bool hit, double e0, double e1)
    {
        Assert.Equal(hit, CamSafetyReview.SegmentRectInterval(x0, y0, x1, y1, l, t, r, b, out var t0, out var t1));
        if (!hit) return;
        Assert.Equal(e0, t0, 3);
        Assert.Equal(e1, t1, 3);
    }

    [Fact]
    public void SegmentRectInterval_fails_closed_on_NaN()
    {
        Assert.True(CamSafetyReview.SegmentRectInterval(double.NaN, 0, 1, 1, 0, 0, 1, 1, out var t0, out var t1));
        Assert.Equal(0, t0); Assert.Equal(1, t1);
    }
}
