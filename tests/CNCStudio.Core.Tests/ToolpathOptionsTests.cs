using System.Globalization;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

public class WorkZeroTests
{
    [Fact]
    public void Front_left_zero_mirrors_y()
    {
        var doc = new CncDocument { MaterialWidth = 600, MaterialHeight = 400 };
        Assert.Equal((0.0, 400.0), doc.ToWork(0, 0));     // back-left corner in the design
        Assert.Equal((600.0, 0.0), doc.ToWork(600, 400)); // front-right
    }

    [Fact]
    public void Centre_zero_is_the_middle_of_the_material()
    {
        var doc = new CncDocument { MaterialWidth = 600, MaterialHeight = 400, WorkZero = WorkZero.Center };
        Assert.Equal((0.0, 0.0), doc.ToWork(300, 200));
        Assert.Equal((-300.0, 200.0), doc.ToWork(0, 0));
    }

    [Fact]
    public void G_code_with_centre_zero_is_centred_and_says_so()
    {
        var doc = new CncDocument { MaterialWidth = 600, MaterialHeight = 600, WorkZero = WorkZero.Center };
        doc.Motion.StockTopMachineZ = -40;
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 250, Y = 250, Width = 100, Height = 100 };
        doc.Shapes.Add(s); doc.CamOperations.Add(new CamOperation { ShapeId = s.Id });
        var r = GCodeGenerator.Generate(doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
        Assert.True(r.Success, string.Join("\n", r.BlockingReasons.Concat(r.VerifierIssues)));
        Assert.Contains(r.Lines, l => l.Contains("MITT"));
        var xs = r.Lines.Where(l => l.StartsWith("G1 X")).Select(l => double.Parse(l.Split(' ')[1][1..], CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(-xs.Min(), xs.Max(), 2); // symmetric around X0
    }
}

public class MillingDirectionTests
{
    private static ContourPreview Review(DesignShape s, CamOperation op)
    {
        var doc = new CncDocument();
        op.ShapeId = s.Id; doc.Shapes.Add(s); doc.CamOperations.Add(op);
        return new CamPlanner(ContourToolpaths.Generate).Review(doc).Operations[0].Preview!;
    }

    private static DesignShape Rect() => new() { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 100, Height = 60 };

    // Machine coordinates: positive area = counter-clockwise (CCW), negative = clockwise (CW).
    [Theory]
    [InlineData(CamOperationKind.Outside, MillingDirection.Climb, -1)]        // part on the right → CW
    [InlineData(CamOperationKind.Outside, MillingDirection.Conventional, 1)]
    [InlineData(CamOperationKind.Inside, MillingDirection.Climb, 1)]          // surroundings on the right → CCW
    [InlineData(CamOperationKind.Inside, MillingDirection.Conventional, -1)]
    public void Contour_direction_follows_the_wall_side(CamOperationKind kind, MillingDirection dir, int sign)
    {
        var p = Review(Rect(), new CamOperation { Kind = kind, Direction = dir });
        Assert.Equal(sign, Math.Sign(CamDirection.MachineArea(p.Centerline)));
    }

    [Fact]
    public void Ellipse_outside_climb_is_clockwise()
    {
        var e = new DesignShape { Kind = ShapeKind.Ellipse, X = 100, Y = 100, Width = 80, Height = 50 };
        Assert.True(CamDirection.MachineArea(Review(e, new CamOperation { Direction = MillingDirection.Climb }).Centerline) < 0);
    }

    [Fact]
    public void Pocket_climb_runs_ccw_along_the_wall_and_cw_around_islands()
    {
        var frame = new DesignShape
        {
            Kind = ShapeKind.VectorPath, X = 100, Y = 100, Width = 100, Height = 100,
            Contours =
            {
                new VectorContour { Points = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) } },
                new VectorContour { Points = { new(0.3, 0.3), new(0.7, 0.3), new(0.7, 0.7), new(0.3, 0.7) } }
            }
        };
        var p = Review(frame, new CamOperation { Kind = CamOperationKind.Pocket, UseTabs = false, ToolDiameter = 6,
            Direction = MillingDirection.Climb });
        // The outermost finishing loop follows the pocket wall (wall outside the loop): CCW.
        var wallLoop = p.Centerlines.OrderByDescending(l => Math.Abs(CamDirection.MachineArea(l))).First();
        Assert.True(CamDirection.MachineArea(wallLoop) > 0);
        // The loop hugging the island (smallest loop that encloses the island centre) has the wall inside: CW.
        var islandLoop = p.Centerlines
            .Where(l => CamGeometryChecks.Inside(new CamPoint(150, 150), new[] { l }))
            .OrderBy(l => Math.Abs(CamDirection.MachineArea(l))).First();
        Assert.True(CamDirection.MachineArea(islandLoop) < 0);
    }

    [Fact]
    public void Reversing_keeps_the_start_point_and_tabs_still_fit()
    {
        var doc = new CncDocument();
        var s = Rect(); doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, TotalDepth = 18, DepthPerPass = 3, UseTabs = true,
            Direction = MillingDirection.Conventional });
        var r = new CamPlanner(ContourToolpaths.Generate).Review(doc).Operations[0];
        Assert.Equal(r.Preview!.Centerline[0], r.Preview.Centerline[^1]);
        Assert.Equal(4, r.TabSpans.Count);
    }

    [Fact]
    public void Changing_direction_recomputes_the_path()
    {
        var doc = new CncDocument();
        var s = Rect(); doc.Shapes.Add(s);
        var op = new CamOperation { ShapeId = s.Id, Direction = MillingDirection.Climb };
        doc.CamOperations.Add(op);
        var planner = new CamPlanner(ContourToolpaths.Generate);
        var a = CamDirection.MachineArea(planner.Review(doc).Operations[0].Preview!.Centerline);
        op.Direction = MillingDirection.Conventional;
        var b = CamDirection.MachineArea(planner.Review(doc).Operations[0].Preview!.Centerline);
        Assert.Equal(-Math.Sign(a), Math.Sign(b));
    }
}

public class RampTests
{
    private static (CncDocument Doc, CamReport Report) Plan(CamOperation op, DesignShape? shape = null)
    {
        var doc = new CncDocument();
        doc.Motion.StockTopMachineZ = -40;
        var s = shape ?? new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 100, Height = 60 };
        op.ShapeId = s.Id; doc.Shapes.Add(s); doc.CamOperations.Add(op);
        return (doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
    }

    [Fact]
    public void Ramp_descends_along_the_path_at_the_set_angle()
    {
        var (_, report) = Plan(new CamOperation { Plunge = PlungeMode.Ramp, RampAngleDeg = 3, TotalDepth = 6, DepthPerPass = 2 });
        var ramps = report.Motion.Moves.Where(m => m.Purpose.StartsWith("Rampad")).ToList();
        Assert.Equal(3, ramps.Count);
        Assert.Equal((0.0, -2.0), (ramps[0].Z0, ramps[0].Z1));   // first ramp starts at the surface
        Assert.Equal((-2.0, -4.0), (ramps[1].Z0, ramps[1].Z1));  // then from the previous depth
        Assert.All(ramps, r => Assert.Equal(2 / Math.Tan(3 * Math.PI / 180), r.S1 - r.S0, 6));
    }

    [Fact]
    public void Ramp_never_plunges_straight_into_material()
    {
        var (_, report) = Plan(new CamOperation { Plunge = PlungeMode.Ramp, TotalDepth = 6, DepthPerPass = 2 });
        var verticalIntoMaterial = report.Motion.Moves.Where(m => m.Kind == MoveKind.Feed && !m.MovesXY && m.Z1 < m.Z0 && m.Z1 < -1e-9);
        Assert.Empty(verticalIntoMaterial);
    }

    [Fact]
    public void Each_depth_is_still_cut_all_the_way_round()
    {
        var (_, report) = Plan(new CamOperation { Plunge = PlungeMode.Ramp, TotalDepth = 6, DepthPerPass = 2 });
        double length = PathMath.Length(report.Operations[0].Preview!.Centerline);
        foreach (var depth in new[] { -2.0, -4.0, -6.0 })
        {
            double atDepth = report.Motion.Moves.Where(m => m.FollowsPath && m.Z0 == depth && m.Z1 == depth).Sum(m => m.S1 - m.S0);
            Assert.Equal(length, atDepth, 6);
        }
    }

    [Fact]
    public void Steep_ramp_on_a_short_path_uses_the_whole_loop()
    {
        var small = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 5, Height = 5 };
        var (_, report) = Plan(new CamOperation { Plunge = PlungeMode.Ramp, RampAngleDeg = 2, TotalDepth = 2, DepthPerPass = 2 }, small);
        var ramp = Assert.Single(report.Motion.Moves, m => m.Purpose.StartsWith("Rampad"));
        Assert.Equal(PathMath.Length(report.Operations[0].Preview!.Centerline), ramp.S1, 6);
    }

    [Fact]
    public void Passes_that_reach_tabs_use_a_straight_plunge()
    {
        var (_, report) = Plan(new CamOperation { Plunge = PlungeMode.Ramp, TotalDepth = 18, DepthPerPass = 3, UseTabs = true });
        // Tabs are 3 mm high: passes at −3…−15 ramp, the pass at −18 goes below the tab top (−15).
        Assert.Equal(5, report.Motion.Moves.Count(m => m.Purpose.StartsWith("Rampad")));
        Assert.Contains(report.Motion.Moves, m => m.Purpose == "Nedmatning till 18 mm");
    }

    [Fact]
    public void Pocket_ramps_into_each_level()
    {
        var (_, report) = Plan(new CamOperation { Kind = CamOperationKind.Pocket, UseTabs = false, Plunge = PlungeMode.Ramp,
            ToolDiameter = 6, TotalDepth = 6, DepthPerPass = 2 });
        Assert.Equal(3, report.Motion.Moves.Count(m => m.Purpose.StartsWith("Rampad")));
        Assert.Contains(report.Motion.Moves, m => m.Purpose == "Ned till föregående djup");
    }

    [Fact]
    public void Ramped_g_code_passes_the_verifier_and_interpolates_z()
    {
        var (doc, report) = Plan(new CamOperation { Plunge = PlungeMode.Ramp, TotalDepth = 6, DepthPerPass = 2 });
        var r = GCodeGenerator.Generate(doc, report);
        Assert.True(r.Success, string.Join("\n", r.BlockingReasons.Concat(r.VerifierIssues)));
        var rampLines = r.Lines.Where(l => l.StartsWith("G1 X") && l.Contains(" Z")).ToList();
        Assert.NotEmpty(rampLines);
        var zs = rampLines.Select(l => double.Parse(l.Split(' ').First(w => w.StartsWith("Z"))[1..], CultureInfo.InvariantCulture)).ToList();
        Assert.All(zs, z => Assert.InRange(z, -6, 0));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(50)]
    public void Ramp_angle_outside_1_to_45_is_refused(double angle) =>
        Assert.NotNull(CamValidation.GetError(new CamOperation { Plunge = PlungeMode.Ramp, RampAngleDeg = angle }, 18));
}

public class RunTimeTests
{
    [Fact]
    public void Cutting_time_is_distance_over_feed()
    {
        var doc = new CncDocument();
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 100, Height = 60 };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, ToolDiameter = 3.175, TotalDepth = 6, DepthPerPass = 2,
            FeedRate = 1000, PlungeRate = 300 });
        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc);
        var t = RunTimeEstimator.Estimate(doc, report);
        double perimeter = 2 * (103.175 + 63.175);
        // Plunges start 2 mm above the surface (plunge clearance): 2 + 6 = 8 mm at the plunge rate.
        Assert.Equal(3 * perimeter / 1000 + 8.0 / 300, t.CuttingMinutes, 3);
        Assert.True(t.RapidRateAssumed);
        Assert.True(t.RapidMinutes > 0);
        Assert.Equal(1, t.ManualPauses); // default spindle is manual
    }

    [Fact]
    public void Known_max_rates_are_used_and_grbl_spindle_adds_spin_up()
    {
        var doc = new CncDocument();
        doc.Machine.MaxRateX = 6000; doc.Machine.MaxRateY = 6000; doc.Machine.MaxRateZ = 1000;
        doc.Machine.Spindle = SpindleControl.Grbl;
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 100, Height = 60 };
        doc.Shapes.Add(s); doc.CamOperations.Add(new CamOperation { ShapeId = s.Id });
        var t = RunTimeEstimator.Estimate(doc, new CamPlanner(ContourToolpaths.Generate).Review(doc));
        Assert.False(t.RapidRateAssumed);
        Assert.Equal(4.0 / 60, t.DwellMinutes, 6);
        Assert.Equal(0, t.ManualPauses);
    }

    [Theory]
    [InlineData(0.4, "< 1 min")]
    [InlineData(12.2, "13 min")]
    [InlineData(95, "1 h 35 min")]
    public void Times_are_formatted_readably(double minutes, string expected) =>
        Assert.Equal(expected, RunTimeEstimator.Format(minutes));
}

public class ToolLibraryTests
{
    [Fact]
    public void Default_tools_are_valid()
    {
        var lib = ToolLibrary.Defaults();
        Assert.NotEmpty(lib.Tools);
        Assert.All(lib.Tools, t => Assert.Null(t.Validate()));
        Assert.Contains(lib.Tools, t => t.Kind == ToolKind.VBit);
    }

    [Fact]
    public void Library_survives_save_and_load_and_drops_invalid_tools()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tools-{Guid.NewGuid():N}.json");
        try
        {
            var lib = ToolLibrary.Defaults();
            lib.Tools.Add(new ToolDefinition { Name = "Min fräs", Diameter = 4.2, Flutes = 3 });
            lib.Save(path);
            var loaded = ToolLibrary.Load(path);
            Assert.Contains(loaded.Tools, t => t.Name == "Min fräs" && t.Diameter == 4.2 && t.Flutes == 3);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"Diameter\": 4.2", "\"Diameter\": -1"));
            Assert.DoesNotContain(ToolLibrary.Load(path).Tools, t => t.Name == "Min fräs");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_library_file_gives_defaults()
    {
        Assert.NotEmpty(ToolLibrary.Load(Path.Combine(Path.GetTempPath(), $"none-{Guid.NewGuid():N}.json")).Tools);
    }

    [Fact]
    public void Saving_an_invalid_tool_is_refused()
    {
        var lib = new ToolLibrary { Tools = { new ToolDefinition { Name = "", Diameter = 3 } } };
        Assert.Throws<InvalidDataException>(() => lib.Save(Path.Combine(Path.GetTempPath(), $"x-{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void Applying_a_tool_fills_the_operation()
    {
        var op = new CamOperation();
        new ToolDefinition { Name = "6 mm", Diameter = 6, FeedRate = 1500, PlungeRate = 400, DepthPerPass = 3, SpindleRpm = 16000 }.ApplyTo(op);
        Assert.Equal(("6 mm", 6.0, 1500.0, 400.0, 3.0, 16000.0),
            (op.ToolName, op.ToolDiameter, op.FeedRate, op.PlungeRate, op.DepthPerPass, op.SpindleRpm));
    }

    [Fact]
    public void V_bit_for_contour_is_critical_and_ball_nose_is_info()
    {
        var shape = new DesignShape { Kind = ShapeKind.Rectangle, Width = 50, Height = 50 };
        Assert.Contains(CamOperationChecks.Analyze(new CamOperation { ToolKind = ToolKind.VBit }, shape, 18),
            f => f.Severity == CamSeverity.Critical && f.Code == "VBitNotSupported");
        Assert.Contains(CamOperationChecks.Analyze(new CamOperation { ToolKind = ToolKind.BallNose }, shape, 18),
            f => f.Severity == CamSeverity.Info && f.Code == "BallNoseFloor");
    }

    [Fact]
    public void Duplicating_an_operation_keeps_the_new_settings()
    {
        var op = new CamOperation { ToolName = "X", ToolKind = ToolKind.BallNose, Plunge = PlungeMode.Ramp,
            RampAngleDeg = 5, Direction = MillingDirection.Climb, StepoverPercent = 35 };
        var copy = op.CopyFor(Guid.NewGuid());
        Assert.Equal((op.ToolName, op.ToolKind, op.Plunge, op.RampAngleDeg, op.Direction, op.StepoverPercent),
            (copy.ToolName, copy.ToolKind, copy.Plunge, copy.RampAngleDeg, copy.Direction, copy.StepoverPercent));
    }
}
