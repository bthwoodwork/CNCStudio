using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>Nolla X/Y enligt materialplaceringen: the machine position of X0 Y0 and the G10 line that sets it.</summary>
public class WorkZeroPlacementTests
{
    private static Dictionary<int, double> Settings(int mask) => new() { [23] = mask, [130] = 500, [131] = 500, [132] = 100 };

    private static CncDocument Doc(int mask, WorkZero zero = WorkZero.FrontLeft)
    {
        var d = new CncDocument { MaterialWidth = 409, MaterialHeight = 409, MaterialThickness = 18, WorkZero = zero };
        d.Machine.WorkWidthMm = 500; d.Machine.WorkLengthMm = 500; d.Machine.HomingDirInvertMask = mask;
        d.Stock.X = 45.5; d.Stock.Y = 45.5;
        return d;
    }

    [Fact]
    public void The_surfacing_area_from_the_screenshot_gets_its_zero_45_5_mm_in_from_home()
    {
        // Home front-left ($23=3), area 45.5 mm in from it: X0 Y0 at MPos −454.5, −454.5.
        var doc = Doc(3);
        var fromHome = WorkZeroPlacement.PointFromHome(doc);
        Assert.Equal((45.5, 45.5), fromHome);
        Assert.Equal((-454.5, -454.5), WorkZeroPlacement.MachinePosition(fromHome.X, fromHome.Y, Settings(3)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void The_machine_position_is_where_the_tool_marker_shows_the_same_numbers(int mask)
    {
        var s = Settings(mask);
        var (mx, my) = WorkZeroPlacement.MachinePosition(120, 80, s);
        Assert.InRange(mx, -500, 0); Assert.InRange(my, -500, 0);
        Assert.Equal((120.0, 80.0), Commissioning.DistanceFromSwitches(new[] { mx, my, 0 }, s));
    }

    [Fact]
    public void A_centre_zero_is_the_middle_of_the_material()
    {
        var doc = Doc(3, WorkZero.Center);
        Assert.Equal((45.5 + 204.5, 45.5 + 204.5), WorkZeroPlacement.PointFromHome(doc));
    }

    [Fact]
    public void The_G10_line_makes_the_zero_land_there_without_moving()
    {
        // Tool standing at the home position (MPos −499, −499): it becomes X −44.5 Y −44.5, so X0 Y0 is at −454.5.
        var line = WorkZeroPlacement.SetLine(new double[] { -499, -499, -20 }, (-454.5, -454.5));
        Assert.Equal("G10 L20 P0 X-44.5 Y-44.5", line);
    }

    [Fact(Timeout = 60000)]
    public async Task On_the_simulated_machine_the_offset_lands_on_the_computed_machine_position()
    {
        var sim = new SimulatedGrbl(speedFactor: 60);
        using var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(30))).IsOk);
        var settings = await client.ReadSettingsAsync();
        var status = await client.RequestStatusAsync(TimeSpan.FromSeconds(1));
        var mpos = status!.MPos ?? throw new InvalidOperationException("ingen MPos");
        var zero = WorkZeroPlacement.MachinePosition(45.5, 45.5, settings);
        Assert.Null(WorkZeroPlacement.Check(zero, settings, mpos));
        Assert.True((await client.SendLineAsync(WorkZeroPlacement.SetLine(mpos, zero))).IsOk);
        var p = await client.ReadParametersAsync();
        Assert.Equal(zero.X, p["G54"][0], 3);
        Assert.Equal(zero.Y, p["G54"][1], 3);
        // And the tool marker would show 45.5 / 45.5 at that point.
        Assert.Equal((45.5, 45.5), Commissioning.DistanceFromSwitches(new[] { zero.X, zero.Y, 0 }, settings));
    }

    [Fact]
    public void A_zero_outside_the_machine_or_an_unknown_position_is_refused()
    {
        var s = Settings(3);
        Assert.NotNull(WorkZeroPlacement.Check((10, -100), s, new double[] { -1, -1, -1 }));
        Assert.NotNull(WorkZeroPlacement.Check((-100, -100), s, null));
        Assert.NotNull(WorkZeroPlacement.Check((-100, -100), new Dictionary<int, double>(), new double[] { -1, -1, -1 }));
        Assert.Null(WorkZeroPlacement.Check((-454.5, -454.5), s, new double[] { -499, -499, -20 }));
    }
}
