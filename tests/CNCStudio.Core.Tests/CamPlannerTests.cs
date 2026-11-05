using CNCStudio.Core;

namespace CNCStudio.Core.Tests;

public class CamPlannerTests
{
    private static CncDocument DocWithRect(out DesignShape shape, out CamOperation op)
    {
        var doc = new CncDocument();
        shape = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 200, Height = 150 };
        op = new CamOperation { ShapeId = shape.Id };
        doc.Shapes.Add(shape); doc.CamOperations.Add(op);
        return doc;
    }

    /// <summary>Vector shape with more points than the auto-recompute limit.</summary>
    private static DesignShape HeavyShape()
    {
        var contour = new VectorContour();
        int n = CamPlanner.AutoRecomputePointLimit + 1;
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / n;
            contour.Points.Add(new VectorPoint(0.5 + 0.5 * Math.Cos(a), 0.5 + 0.5 * Math.Sin(a)));
        }
        return new DesignShape { Kind = ShapeKind.VectorPath, X = 100, Y = 100, Width = 100, Height = 100,
            Contours = { contour } };
    }

    /// <summary>Stand-in for the WPF vector generator: a square around the shape bounds.</summary>
    private static ContourPreview FakeGenerator(DesignShape s, CamOperation op, double t) =>
        ContourToolpaths.Generate(new DesignShape { Kind = ShapeKind.Rectangle, X = s.X, Y = s.Y,
            Width = s.Width, Height = s.Height }, op, t);

    [Fact]
    public void Clean_operation_is_analyzed_without_findings()
    {
        var doc = DocWithRect(out _, out _);
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc);
        var result = Assert.Single(report.Operations);
        Assert.True(result.Analyzed);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Unchanged_shape_reuses_the_cached_path()
    {
        var doc = DocWithRect(out _, out _);
        var planner = new CamPlanner(ContourToolpaths.Generate);
        planner.Review(doc); planner.Review(doc); planner.Review(doc);
        Assert.Equal(1, planner.GenerationCount);
    }

    [Fact]
    public void Moving_a_fixture_or_the_stock_does_not_regenerate_but_is_rechecked()
    {
        var doc = DocWithRect(out _, out _);
        var planner = new CamPlanner(ContourToolpaths.Generate);
        planner.Review(doc);
        var fixture = new FixtureBlock { X = 600, Y = 600, Width = 30, Length = 30 };
        doc.Fixtures.Add(fixture);
        Assert.Empty(planner.Review(doc).Operations.SelectMany(o => o.Findings));
        fixture.X = 140; fixture.Y = 140; // over the path's corner: hits the top and left edges
        var hits = planner.Review(doc).AllFindings.Where(f => f.Severity == CamSeverity.Critical).ToList();
        Assert.Equal(2, hits.Count);
        Assert.All(hits, f => Assert.Equal("FixtureCollision", f.Code));
        doc.Stock.X = 300;
        planner.Review(doc);
        Assert.Equal(1, planner.GenerationCount);
    }

    [Fact]
    public void Changing_a_cheap_shape_regenerates_automatically()
    {
        var doc = DocWithRect(out var shape, out _);
        var planner = new CamPlanner(ContourToolpaths.Generate);
        planner.Review(doc);
        shape.Width = 250;
        var result = Assert.Single(planner.Review(doc).Operations);
        Assert.Equal(2, planner.GenerationCount);
        Assert.Equal(100 + 250 + 3.175 / 2, result.Preview!.Centerline[1].X, 6);
    }

    [Fact]
    public void Changing_cam_settings_or_thickness_regenerates()
    {
        var doc = DocWithRect(out _, out var op);
        var planner = new CamPlanner(ContourToolpaths.Generate);
        planner.Review(doc);
        op.ToolDiameter = 6; planner.Review(doc);
        doc.MaterialThickness = 20; planner.Review(doc);
        Assert.Equal(3, planner.GenerationCount);
    }

    [Fact]
    public void Heavy_shape_is_computed_once_then_marked_stale_after_a_change()
    {
        var doc = new CncDocument();
        var shape = HeavyShape();
        doc.Shapes.Add(shape); doc.CamOperations.Add(new CamOperation { ShapeId = shape.Id });
        var planner = new CamPlanner(FakeGenerator);

        Assert.True(Assert.Single(planner.Review(doc).Operations).Analyzed); // first time: computed
        shape.Width += 10; // resize changes the toolpath's shape
        var stale = Assert.Single(planner.Review(doc).Operations);
        Assert.True(stale.Stale);
        Assert.False(stale.Analyzed); // an outdated path is never reported as checked
        Assert.Contains(stale.Findings, f => f.Code == "Stale");
        Assert.Equal(1, planner.GenerationCount);

        var fresh = Assert.Single(planner.Review(doc, computeAll: true).Operations);
        Assert.True(fresh.Analyzed);
        Assert.False(fresh.Stale);
        Assert.Equal(2, planner.GenerationCount);
    }

    [Fact]
    public void Moving_a_heavy_shape_translates_the_path_without_recomputing()
    {
        var doc = new CncDocument();
        var shape = HeavyShape();
        doc.Shapes.Add(shape); doc.CamOperations.Add(new CamOperation { ShapeId = shape.Id });
        var planner = new CamPlanner(FakeGenerator);
        var before = Assert.Single(planner.Review(doc).Operations).Preview!.Centerline[0];
        shape.X += 25; shape.Y -= 5;
        var after = Assert.Single(planner.Review(doc).Operations);
        Assert.True(after.Analyzed);
        Assert.False(after.Stale);
        Assert.Equal(1, planner.GenerationCount);
        Assert.Equal(before.X + 25, after.Preview!.Centerline[0].X, 9);
        Assert.Equal(before.Y - 5, after.Preview.Centerline[0].Y, 9);
        Assert.Equal(after.Preview.Centerline[0], after.Preview.Centerlines[0][0]);
    }

    [Fact]
    public void Translated_path_matches_a_freshly_computed_one()
    {
        var doc = DocWithRect(out var shape, out var op);
        var planner = new CamPlanner(ContourToolpaths.Generate);
        planner.Review(doc);
        shape.X = 333.3; shape.Y = 41.7;
        var moved = Assert.Single(planner.Review(doc).Operations).Preview!;
        var fresh = ContourToolpaths.Generate(shape, op, doc.MaterialThickness);
        for (int i = 0; i < fresh.Centerline.Count; i++)
        {
            Assert.Equal(fresh.Centerline[i].X, moved.Centerline[i].X, 9);
            Assert.Equal(fresh.Centerline[i].Y, moved.Centerline[i].Y, 9);
        }
    }

    [Fact]
    public void Moving_a_shape_into_a_fixture_is_detected_without_recomputing()
    {
        var doc = DocWithRect(out var shape, out _);
        // Fixture at machine X 440..470: straddles the path's left edge (X ≈ 448.4) once moved.
        doc.Fixtures.Add(new FixtureBlock { X = 440, Y = 500, Width = 30, Length = 30 });
        var planner = new CamPlanner(ContourToolpaths.Generate);
        Assert.Equal(0, planner.Review(doc).CriticalCount);
        shape.X = 400; shape.Y = 400; // stock at (50,50): path's left edge now crosses the fixture
        Assert.True(planner.Review(doc).CriticalCount > 0);
        Assert.Equal(1, planner.GenerationCount);
    }

    [Fact]
    public void Heavy_shape_can_be_computed_individually()
    {
        var doc = new CncDocument();
        var shape = HeavyShape();
        doc.Shapes.Add(shape); doc.CamOperations.Add(new CamOperation { ShapeId = shape.Id });
        var planner = new CamPlanner(FakeGenerator);
        planner.Review(doc);
        shape.Width = 120;
        Assert.True(Assert.Single(planner.Review(doc, computeShapes: new[] { shape.Id }).Operations).Analyzed);
    }

    [Fact]
    public void Generator_failure_is_critical_and_not_analyzed()
    {
        var doc = DocWithRect(out var shape, out var op);
        shape.Width = 3; shape.Height = 3;
        op.Kind = CamOperationKind.Inside; op.ToolDiameter = 6; op.TotalDepth = 2; op.DepthPerPass = 1;
        var result = Assert.Single(new CamPlanner(ContourToolpaths.Generate).Review(doc).Operations);
        Assert.False(result.Analyzed);
        Assert.Contains(result.Findings, f => f.Severity == CamSeverity.Critical && f.Code == "NotAnalyzed");
    }

    [Fact]
    public void Generator_exceptions_of_any_type_fail_closed()
    {
        var doc = DocWithRect(out _, out _);
        var planner = new CamPlanner((_, _, _) => throw new InvalidOperationException("boom"));
        var result = Assert.Single(planner.Review(doc).Operations);
        Assert.False(result.Analyzed);
        Assert.Equal(CamSeverity.Critical, result.Worst);
    }

    [Fact]
    public void Depth_deeper_than_material_is_critical_without_calling_generator()
    {
        var doc = DocWithRect(out _, out var op);
        op.TotalDepth = 25; // material is 18 mm
        var planner = new CamPlanner(ContourToolpaths.Generate);
        var result = Assert.Single(planner.Review(doc).Operations);
        Assert.Contains(result.Findings, f => f.Severity == CamSeverity.Critical && f.Code == "InvalidSettings");
        Assert.Equal(0, planner.GenerationCount);
    }

    [Fact]
    public void Missing_shape_is_critical()
    {
        var doc = new CncDocument();
        doc.CamOperations.Add(new CamOperation { ShapeId = Guid.NewGuid() });
        var result = Assert.Single(new CamPlanner(ContourToolpaths.Generate).Review(doc).Operations);
        Assert.Contains(result.Findings, f => f.Severity == CamSeverity.Critical && f.Code == "MissingShape");
    }

    [Fact]
    public void Fingerprint_ignores_stock_and_position_but_tracks_geometry()
    {
        var doc = DocWithRect(out var shape, out var op);
        var before = CamPlanner.Fingerprint(shape, op, doc.MaterialThickness);
        doc.Stock.X = 400; shape.X = 10;
        Assert.Equal(before, CamPlanner.Fingerprint(shape, op, doc.MaterialThickness));
        shape.Height = 151;
        Assert.NotEqual(before, CamPlanner.Fingerprint(shape, op, doc.MaterialThickness));
    }

    [Fact]
    public void Fingerprint_tracks_contour_point_edits()
    {
        var shape = HeavyShape();
        var op = new CamOperation { ShapeId = shape.Id };
        var before = CamPlanner.Fingerprint(shape, op, 18);
        shape.Contours[0].Points[5].X += 0.001;
        Assert.NotEqual(before, CamPlanner.Fingerprint(shape, op, 18));
    }
}

public class CamOperationChecksTests
{
    private static readonly DesignShape Shape = new() { Kind = ShapeKind.Rectangle, Width = 10, Height = 10 };

    [Fact]
    public void Pass_deeper_than_tool_diameter_is_a_warning()
    {
        var f = CamOperationChecks.Analyze(new CamOperation { ToolDiameter = 3.175, TotalDepth = 8, DepthPerPass = 4 }, Shape, 18);
        Assert.Contains(f, x => x.Severity == CamSeverity.Warning && x.Code == "DeepPass");
    }

    [Fact]
    public void Through_cut_without_tabs_warns_about_loose_part()
    {
        var f = CamOperationChecks.Analyze(new CamOperation { TotalDepth = 18, DepthPerPass = 2, UseTabs = false }, Shape, 18);
        Assert.Contains(f, x => x.Severity == CamSeverity.Info && x.Code == "ThroughCut");
        Assert.Contains(f, x => x.Severity == CamSeverity.Warning && x.Code == "LoosePart");
    }

    [Fact]
    public void Through_cut_with_tabs_has_no_loose_part_warning()
    {
        var f = CamOperationChecks.Analyze(new CamOperation { TotalDepth = 18, DepthPerPass = 2, UseTabs = true }, Shape, 18);
        Assert.DoesNotContain(f, x => x.Code == "LoosePart");
        Assert.DoesNotContain(f, x => x.Severity == CamSeverity.Warning);
    }

    [Fact]
    public void Tab_as_high_as_the_cut_is_a_warning()
    {
        var f = CamOperationChecks.Analyze(new CamOperation { TotalDepth = 18, DepthPerPass = 2, TabHeight = 18 }, Shape, 18);
        Assert.Contains(f, x => x.Severity == CamSeverity.Warning && x.Code == "TabTooHigh");
    }

    [Theory]
    [InlineData(17.5, true)]   // 0.5 mm left
    [InlineData(17.0, false)]  // exactly 1 mm left
    [InlineData(10.0, false)]
    public void Thin_floor_under_a_partial_cut_is_a_warning(double depth, bool expected)
    {
        var f = CamOperationChecks.Analyze(new CamOperation { TotalDepth = depth, DepthPerPass = 2 }, Shape, 18);
        Assert.Equal(expected, f.Any(x => x.Code == "ThinFloor"));
    }

    [Theory]
    [InlineData(0, 6, 3)]
    [InlineData(4, 0, 3)]
    [InlineData(4, 6, 0)]
    [InlineData(51, 6, 3)]
    public void Invalid_tab_settings_are_rejected(int count, double width, double height)
    {
        var op = new CamOperation { UseTabs = true, TabCount = count, TabWidth = width, TabHeight = height };
        Assert.NotNull(CamValidation.GetError(op, 18));
    }

    [Fact]
    public void Invalid_tab_settings_are_ignored_when_tabs_are_off()
    {
        var op = new CamOperation { UseTabs = false, TabCount = 0, TabWidth = 0, TabHeight = 0 };
        Assert.Null(CamValidation.GetError(op, 18));
    }

    [Fact]
    public void Normal_partial_depth_gives_no_findings()
    {
        Assert.Empty(CamOperationChecks.Analyze(new CamOperation(), Shape, 18));
    }
}
