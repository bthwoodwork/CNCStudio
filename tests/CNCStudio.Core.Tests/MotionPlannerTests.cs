using CNCStudio.Core;

namespace CNCStudio.Core.Tests;

public class MotionPlannerTests
{
    private static DesignShape Rect(double x, double y, double w = 100, double h = 60) =>
        new() { Kind = ShapeKind.Rectangle, X = x, Y = y, Width = w, Height = h };

    private static CamReport Review(CncDocument doc) => new CamPlanner(ContourToolpaths.Generate).Review(doc);

    private static CncDocument OneRect(double depth = 6, bool tabs = false, double? zMax = 60)
    {
        var doc = new CncDocument();
        doc.Motion.StockTopMachineZ = -zMax;
        var s = Rect(100, 100);
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, TotalDepth = depth, DepthPerPass = 2, UseTabs = tabs });
        return doc;
    }

    [Fact]
    public void Simple_contour_plans_lift_plunge_passes_and_retract()
    {
        var plan = Review(OneRect()).Motion;
        Assert.Equal(10, plan.StartHeight);                     // no fixtures: travel height
        Assert.Equal(10, plan.MaxZ);
        var plunges = plan.Moves.Where(m => m.Purpose.StartsWith("Nedmatning")).Select(m => m.Z1).ToList();
        Assert.Equal(new[] { -2.0, -4.0, -6.0 }, plunges);
        Assert.Equal(-6, plan.Moves.Min(m => Math.Min(m.Z0, m.Z1)));
        Assert.Equal(10, plan.Moves[^1].Z1);                    // ends lifted
    }

    [Fact]
    public void Rapids_never_move_sideways_inside_the_material()
    {
        var doc = OneRect();
        var s2 = Rect(300, 300); doc.Shapes.Add(s2);
        doc.CamOperations.Add(new CamOperation { ShapeId = s2.Id });
        var plan = Review(doc).Motion;
        Assert.All(plan.Moves.Where(m => m.Kind == MoveKind.Rapid && m.MovesXY),
            m => Assert.True(Math.Min(m.Z0, m.Z1) >= doc.Motion.PlungeClearanceMm));
    }

    [Fact]
    public void Each_cut_pass_covers_the_whole_path()
    {
        var report = Review(OneRect());
        double length = PathMath.Length(report.Operations[0].Preview!.Centerline);
        foreach (var depth in new[] { -2.0, -4.0, -6.0 })
        {
            double covered = report.Motion.Moves.Where(m => m.FollowsPath && m.Z0 == depth).Sum(m => m.S1 - m.S0);
            Assert.Equal(length, covered, 6);
        }
    }

    [Fact]
    public void Tool_lifts_over_tabs_only_in_passes_below_the_tab_top()
    {
        // 18 mm through cut, 2 mm passes, 3 mm tabs: tab top at −15, so passes at −16 and −18 lift.
        var plan = Review(OneRect(depth: 18, tabs: true)).Motion;
        var lifts = plan.Moves.Where(m => m.Purpose == "Lyft över hållflik").ToList();
        Assert.Equal(2 * 4, lifts.Count);
        Assert.All(lifts, m => Assert.Equal(-15, m.Z1, 9));
        Assert.All(plan.Moves.Where(m => m.Purpose == "Över hållflik"), m => Assert.Equal(-15, m.Z0, 9));
    }

    [Fact]
    public void Start_height_clears_the_highest_fixture()
    {
        var doc = OneRect();
        doc.Fixtures.Add(new FixtureBlock { X = 600, Y = 600, Width = 30, Length = 30, HeightAboveStockMm = 20, ClearanceMm = 5 });
        var plan = Review(doc).Motion;
        Assert.Equal(25, plan.StartHeight);
        Assert.Equal(25, plan.Moves[0].Z0);
        Assert.Equal(25, plan.Moves[^1].Z1);
    }

    [Fact]
    public void Rapid_crossing_a_fixture_is_raised_above_it()
    {
        // Two parts with a 20 mm high clamp between them (machine coordinates; stock at 50,50).
        var doc = new CncDocument();
        doc.Motion.StockTopMachineZ = -60;
        var a = Rect(100, 100, 50, 50); var b = Rect(300, 100, 50, 50);
        doc.Shapes.Add(a); doc.Shapes.Add(b);
        doc.CamOperations.Add(new CamOperation { ShapeId = a.Id });
        doc.CamOperations.Add(new CamOperation { ShapeId = b.Id });
        doc.Fixtures.Add(new FixtureBlock { Name = "Klämma", X = 50 + 220, Y = 50 + 90, Width = 30, Length = 30,
            HeightAboveStockMm = 20, ClearanceMm = 5 });
        var report = Review(doc);
        var cross = Assert.Single(report.Motion.Moves, m => m.Kind == MoveKind.Rapid && m.MovesXY && m.FixtureName == "Klämma");
        Assert.Equal(25, cross.Z0, 9);
        Assert.Contains(report.GlobalFindings, f => f.Code == "RaisedOverFixture");
    }

    [Fact]
    public void Rapid_not_crossing_a_fixture_stays_at_travel_height()
    {
        var doc = new CncDocument();
        var a = Rect(100, 100, 50, 50); var b = Rect(300, 100, 50, 50);
        doc.Shapes.Add(a); doc.Shapes.Add(b);
        doc.CamOperations.Add(new CamOperation { ShapeId = a.Id });
        doc.CamOperations.Add(new CamOperation { ShapeId = b.Id });
        doc.Fixtures.Add(new FixtureBlock { X = 50 + 220, Y = 50 + 400, Width = 30, Length = 30, HeightAboveStockMm = 20 });
        var travel = Review(doc).Motion.Moves.Where(m => m.Kind == MoveKind.Rapid && m.MovesXY).Skip(0).ToList();
        Assert.Contains(travel, m => m.Z0 == doc.Motion.TravelHeightMm);
        Assert.DoesNotContain(travel, m => m.FixtureName is not null);
    }

    [Fact]
    public void Missing_z_limit_is_a_warning()
    {
        var report = Review(OneRect(zMax: null));
        Assert.Contains(report.GlobalFindings, f => f.Severity == CamSeverity.Warning && f.Code == "ZLimitUnknown");
    }

    [Fact]
    public void Height_above_z_limit_is_critical()
    {
        // Clamp 20 mm + 5 mm clearance needs Z 25, but max is 20 − 3 mm margin = 17.
        var doc = OneRect(zMax: 20);
        doc.Fixtures.Add(new FixtureBlock { X = 600, Y = 600, Width = 30, Length = 30, HeightAboveStockMm = 20, ClearanceMm = 5 });
        var report = Review(doc);
        var f = Assert.Single(report.GlobalFindings, x => x.Code == "ZLimitExceeded");
        Assert.Equal(CamSeverity.Critical, f.Severity);
        Assert.Contains("25", f.Message);
        Assert.Contains("17", f.Message);
    }

    [Fact]
    public void Headroom_is_derived_from_machine_z_after_homing()
    {
        // Homed: Z switch at MPos Z 0. Z zero set on the stock surface at MPos Z −45.2.
        var m = new MotionSettings { StockTopMachineZ = -45.2 };
        Assert.Equal(45.2, m.ZMaxAboveStockMm!.Value, 9);
        Assert.Null(MotionValidation.GetError(m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12.5)]
    [InlineData(-600)]
    public void Machine_z_at_stock_top_must_be_below_the_switch(double mposZ)
    {
        Assert.NotNull(MotionValidation.GetError(new MotionSettings { StockTopMachineZ = mposZ }));
    }

    [Fact]
    public void Clamp_too_high_for_the_available_headroom_is_critical()
    {
        // 45.2 mm up to the switch, 3 mm margin → 42.2 mm allowed; a 40 mm clamp + 5 mm needs 45.
        var doc = OneRect();
        doc.Motion.StockTopMachineZ = -45.2;
        doc.Fixtures.Add(new FixtureBlock { X = 600, Y = 600, Width = 30, Length = 30, HeightAboveStockMm = 40, ClearanceMm = 5 });
        var f = Assert.Single(Review(doc).GlobalFindings, x => x.Code == "ZLimitExceeded");
        Assert.Contains("MPos Z -45.2", f.Message);
    }

    [Fact]
    public void Cut_below_the_bottom_of_z_travel_is_critical()
    {
        // Z travel 65 mm, margin 3: lowest allowed MPos Z is −62. Surface at −58, 6 mm deep → −64.
        var doc = OneRect(depth: 6);
        doc.Machine.ZTravelMm = 65;
        doc.Motion.StockTopMachineZ = -58;
        var f = Assert.Single(Review(doc).GlobalFindings, x => x.Code == "ZBottomExceeded");
        Assert.Equal(CamSeverity.Critical, f.Severity);
        Assert.Contains("MPos Z -64", f.Message);
    }

    [Fact]
    public void Cut_within_z_travel_is_fine()
    {
        var doc = OneRect(depth: 6);
        doc.Machine.ZTravelMm = 65;
        doc.Motion.StockTopMachineZ = -40; // deepest −46, limit −62
        Assert.DoesNotContain(Review(doc).GlobalFindings, f => f.Severity <= CamSeverity.Warning);
    }

    [Fact]
    public void Stock_surface_below_z_travel_is_critical()
    {
        var doc = OneRect();
        doc.Machine.ZTravelMm = 65;
        doc.Motion.StockTopMachineZ = -70;
        Assert.Contains(Review(doc).GlobalFindings, f => f.Code == "StockTopOutsideTravel");
    }

    [Fact]
    public void Limit_margin_comes_from_the_machine_profile()
    {
        // Allowed height = 20 − margin. Travel 10 fits with margin 3 but not with margin 12.
        var doc = OneRect(zMax: 20);
        Assert.DoesNotContain(Review(doc).GlobalFindings, f => f.Code == "ZLimitExceeded");
        doc.Machine.LimitMarginMm = 12;
        Assert.Contains(Review(doc).GlobalFindings, f => f.Code == "ZLimitExceeded");
    }

    [Fact]
    public void Disabled_homing_is_a_warning()
    {
        var doc = OneRect(zMax: 40);
        Assert.DoesNotContain(Review(doc).GlobalFindings, f => f.Code == "NoHoming");
        doc.Machine.HomeOnStartup = false;
        Assert.Contains(Review(doc).GlobalFindings, f => f.Severity == CamSeverity.Warning && f.Code == "NoHoming");
    }

    [Fact]
    public void Travel_height_within_z_limit_is_fine()
    {
        var report = Review(OneRect(zMax: 13)); // travel 10 ≤ 13 − 3
        Assert.DoesNotContain(report.GlobalFindings, f => f.Severity <= CamSeverity.Warning);
    }

    [Fact]
    public void Travel_height_too_close_to_limit_is_critical()
    {
        var report = Review(OneRect(zMax: 12)); // travel 10 > 12 − 3
        Assert.Contains(report.GlobalFindings, f => f.Code == "ZLimitExceeded");
    }

    [Fact]
    public void Inside_contours_are_cut_before_outside_contours()
    {
        var doc = new CncDocument();
        var outer = Rect(100, 100, 200, 200); var hole = Rect(150, 150, 50, 50);
        doc.Shapes.Add(outer); doc.Shapes.Add(hole);
        doc.CamOperations.Add(new CamOperation { ShapeId = outer.Id, Kind = CamOperationKind.Outside });
        doc.CamOperations.Add(new CamOperation { ShapeId = hole.Id, Kind = CamOperationKind.Inside });
        var plan = Review(doc).Motion;
        Assert.Equal(new[] { 1, 0 }, plan.OperationOrder);
        Assert.Equal(1, plan.Moves.First(m => m.FollowsPath).OperationIndex);
    }

    [Fact]
    public void Invalid_motion_settings_are_critical()
    {
        var doc = OneRect();
        doc.Motion.TravelHeightMm = 1; // below the 2 mm plunge clearance
        var report = Review(doc);
        Assert.Contains(report.GlobalFindings, f => f.Severity == CamSeverity.Critical && f.Code == "InvalidMotionSettings");
        Assert.Empty(report.Motion.Moves);
    }

    [Fact]
    public void Operations_without_a_path_are_left_out_with_a_warning()
    {
        var doc = OneRect();
        var tiny = Rect(400, 400, 2, 2); doc.Shapes.Add(tiny);
        doc.CamOperations.Add(new CamOperation { ShapeId = tiny.Id, Kind = CamOperationKind.Inside, ToolDiameter = 6 });
        var report = Review(doc);
        Assert.Contains(report.GlobalFindings, f => f.Code == "MotionIncomplete");
        Assert.DoesNotContain(report.Motion.Moves, m => m.OperationIndex == 1);
    }

    [Fact]
    public void No_operations_means_no_motion_and_no_findings()
    {
        var report = Review(new CncDocument());
        Assert.Empty(report.Motion.Moves);
        Assert.Empty(report.GlobalFindings);
    }

    [Fact]
    public void Old_project_without_motion_settings_loads_with_defaults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{ "Name": "x", "MaterialWidth": 600, "MaterialHeight": 600, "MaterialThickness": 18 }""");
            var doc = DocumentStorage.Load(path);
            Assert.Equal(10, doc.Motion.TravelHeightMm);
            Assert.Null(doc.Motion.StockTopMachineZ);
        }
        finally { File.Delete(path); }
    }
}
