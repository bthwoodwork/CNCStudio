using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>Planfräsa offerskivan: the settings become an ordinary project that passes every check and exports.</summary>
public class WasteboardSurfacingTests
{
    private static MachineProfile Machine(double w = 500, double l = 500, int homeMask = 3) =>
        new() { Name = "X-Carve", WorkWidthMm = w, WorkLengthMm = l, ZTravelMm = 100, HomingDirInvertMask = homeMask };

    private static (CncDocument Doc, CamReport Report) Plan(MachineProfile m, SurfacingSettings s)
    {
        var doc = WasteboardSurfacing.CreateProject(m, s);
        doc.Motion.StockTopMachineZ = -60;            // Z zeroed on the wasteboard's top
        return (doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
    }

    [Fact]
    public void The_largest_area_leaves_room_for_the_cutter_run_out_and_limit_margin()
    {
        var m = Machine();
        var (w, l) = WasteboardSurfacing.MaxArea(m, new SurfacingSettings { ToolDiameter = 25, OverhangMm = 2 });
        Assert.Equal(500.0 - 2 * (25 + 2 + 3) - 1, w);        // the whole cutter clears the edge, then the limit margin
        Assert.Equal(w, l);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void The_whole_table_surfacing_passes_every_check_and_exports_for_every_home_corner(int homeMask)
    {
        var (doc, report) = Plan(Machine(homeMask: homeMask), new SurfacingSettings());
        Assert.DoesNotContain(report.AllFindings, f => f.Severity == CamSeverity.Critical);
        var (jobs, blocking) = GCodeGenerator.GenerateByTool(doc, report);
        Assert.Empty(blocking);
        var job = Assert.Single(jobs);
        Assert.True(job.Result.Success, string.Join("\n", job.Result.VerifierIssues.Concat(job.Result.BlockingReasons)));
        Assert.Equal("Planfräs", job.ToolLabel);
    }

    [Fact]
    public void A_centred_area_lies_in_the_middle_of_the_table()
    {
        var doc = WasteboardSurfacing.CreateProject(Machine(750, 500), new SurfacingSettings { Width = 300, Length = 200 });
        Assert.Equal((225.0, 150.0), (doc.Stock.X, doc.Stock.Y));
        Assert.Equal((300.0, 200.0, 18.0), (doc.MaterialWidth, doc.MaterialHeight, doc.MaterialThickness));
        Assert.Equal(WasteboardSurfacing.MaterialName, doc.MaterialName);
        var op = Assert.Single(doc.CamOperations);
        Assert.Equal(CamOperationKind.Face, op.Kind);
        Assert.False(op.UseTabs);
    }

    [Theory]
    [InlineData(3, 40.0, 410.0)]     // home front-left: 60 mm from the front edge → canvas Y = 500 − 60 − 30
    [InlineData(0, 220.0, 60.0)]     // home back-right: 40 mm from the right edge, 60 mm from the back
    public void A_placed_area_is_measured_from_the_home_corner(int homeMask, double stockX, double stockY)
    {
        var doc = WasteboardSurfacing.CreateProject(Machine(homeMask: homeMask),
            new SurfacingSettings { Width = 240, Length = 30, FromHomeX = 40, FromHomeY = 60 });
        Assert.Equal((stockX, stockY), (doc.Stock.X, doc.Stock.Y));
        Assert.Equal((40.0, 60.0), WasteboardSurfacing.FromHome(doc));
    }

    [Fact]
    public void An_area_too_close_to_the_edge_is_moved_in_so_the_cutter_stays_inside()
    {
        var doc = WasteboardSurfacing.CreateProject(Machine(), new SurfacingSettings { Width = 100, Length = 100, FromHomeX = 0, FromHomeY = 0 });
        Assert.Equal((30.0, 30.0), WasteboardSurfacing.FromHome(doc));   // 25 cutter + 2 run-out + 3 limit margin
    }

    [Fact]
    public void Wrong_settings_are_refused()
    {
        var m = Machine();
        Assert.NotNull(WasteboardSurfacing.Validate(m, new SurfacingSettings { Width = 1000 }));          // larger than the table allows
        Assert.NotNull(WasteboardSurfacing.Validate(m, new SurfacingSettings { TotalDepth = 8 }));         // surfacing, not replacing
        Assert.NotNull(WasteboardSurfacing.Validate(m, new SurfacingSettings { TotalDepth = 1, WasteboardThickness = 1 }));
        Assert.NotNull(WasteboardSurfacing.Validate(m, new SurfacingSettings { ToolDiameter = 0 }));
        Assert.Null(WasteboardSurfacing.Validate(m, new SurfacingSettings()));
        Assert.Throws<ArgumentException>(() => WasteboardSurfacing.CreateProject(m, new SurfacingSettings { Width = 1000 }));
    }

    [Theory]
    [InlineData(3, 40.0, 60.0)]      // home front-left: X0 Y0 is the area's own nearest corner
    [InlineData(0, 280.0, 90.0)]     // home back-right: the front-left corner is 240 + 40 from the right and 30 + 60 from the back
    public void The_instructions_say_where_X0_Y0_is_from_the_home_corner(int homeMask, double x, double y)
    {
        var doc = WasteboardSurfacing.CreateProject(Machine(homeMask: homeMask),
            new SurfacingSettings { Width = 240, Length = 30, FromHomeX = 40, FromHomeY = 60 });
        Assert.Equal((x, y), WasteboardSurfacing.WorkZeroFromHome(doc));
    }
}
