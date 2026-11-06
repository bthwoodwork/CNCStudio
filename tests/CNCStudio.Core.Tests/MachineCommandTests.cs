using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

public class MachineCommandTests
{
    private static GrblStatus Status(string state, double x = -100, double y = -100, double z = -20) =>
        new() { State = state, MPos = new[] { x, y, z } };

    private static MachineProfile Machine() => new() { WorkWidthMm = 750, WorkLengthMm = 750, ZTravelMm = 65, LimitMarginMm = 3, HomingPullOffMm = 1 };

    [Fact]
    public void Jog_line_is_incremental_metric_and_always_has_a_feed()
    {
        Assert.Equal("$J=G91 G21 X5 F3000", GrblMachineCommands.Jog(5, 0, 0, 3000));
        Assert.Equal("$J=G91 G21 Y-0.5 Z0.1 F1000", GrblMachineCommands.Jog(0, -0.5, 0.1, 1000));
        Assert.Throws<ArgumentException>(() => GrblMachineCommands.Jog(0, 0, 0, 1000));
        Assert.Throws<ArgumentException>(() => GrblMachineCommands.Jog(1, 0, 0, 0));
    }

    [Fact]
    public void Jog_lines_pass_the_console_line_check()
    {
        Assert.Null(GrblProtocol.CheckCommandLine(GrblMachineCommands.Jog(-123.456, 78.9, -0.1, 5000)));
    }

    [Theory]
    [InlineData("Idle", true)]
    [InlineData("Jog", true)]
    [InlineData("Alarm", false)]
    [InlineData("Run", false)]
    [InlineData("Hold", false)]
    [InlineData("Home", false)]
    public void Jog_only_in_idle_or_jog(string state, bool ok) => Assert.Equal(ok, GrblMachineCommands.StateAllowsJog(state));

    [Fact]
    public void Jog_in_alarm_tells_the_user_to_home() =>
        Assert.Contains("$H", GrblMachineCommands.CheckJog(Status("Alarm"), Machine(), 5, 0, 0));

    [Fact]
    public void Jog_inside_travel_is_allowed() =>
        Assert.Null(GrblMachineCommands.CheckJog(Status("Idle"), Machine(), 50, -50, -10));

    [Theory]
    [InlineData(200, 0, 0)]     // X to +100: past the switch
    [InlineData(0, -700, 0)]    // Y to −800: beyond −750 + 3
    [InlineData(0, 0, -50)]     // Z to −70: below −65 + 3
    [InlineData(0, 0, 19)]      // Z to −1: closer to the switch than the 3 mm margin
    public void Jog_beyond_travel_is_refused(double dx, double dy, double dz) =>
        Assert.NotNull(GrblMachineCommands.CheckJog(Status("Idle"), Machine(), dx, dy, dz));

    [Fact]
    public void After_homing_moving_away_from_the_switch_is_allowed_but_not_towards_it()
    {
        // Just homed: MPos = −$27 = −1, inside the 3 mm margin.
        var homed = Status("Idle", -1, -1, -1);
        Assert.Null(GrblMachineCommands.CheckJog(homed, Machine(), -0.5, 0, 0));   // away from the switch
        Assert.Null(GrblMachineCommands.CheckJog(homed, Machine(), 0, 0, -0.5));
        Assert.NotNull(GrblMachineCommands.CheckJog(homed, Machine(), 0.5, 0, 0)); // towards it
    }

    [Fact]
    public void Without_homing_the_travel_check_is_skipped()
    {
        var m = Machine(); m.HomeOnStartup = false;
        Assert.Null(GrblMachineCommands.CheckJog(Status("Idle"), m, 500, 0, 0));
    }

    [Fact]
    public void Jog_speed_is_limited_by_max_rates()
    {
        var m = Machine(); m.MaxRateX = 4000; m.MaxRateY = 3500; m.MaxRateZ = 800;
        Assert.Equal(3500, GrblMachineCommands.ClampFeed(5000, m, xy: true, z: false));
        Assert.Equal(800, GrblMachineCommands.ClampFeed(5000, m, xy: false, z: true));
        Assert.Equal(5000, GrblMachineCommands.ClampFeed(5000, Machine(), xy: true, z: true));
    }

    [Fact]
    public void Continuous_jog_step_follows_the_wiki_formula()
    {
        // s = v · dt with v = 3000 mm/min = 50 mm/s and dt = 0.05 s.
        Assert.Equal(2.5, GrblMachineCommands.ContinuousStep(3000), 9);
    }

    [Fact]
    public void Zero_uses_the_active_wcs_and_only_named_axes()
    {
        Assert.Equal("G10 L20 P0 X0 Y0", GrblMachineCommands.Zero(true, true, false)); // Z untouched
        Assert.Equal("G10 L20 P0 Z0", GrblMachineCommands.Zero(false, false, true));
        Assert.Equal("G10 L20 P0 X12.5", GrblMachineCommands.SetWork(12.5, null, null));
        Assert.Throws<ArgumentException>(() => GrblMachineCommands.Zero(false, false, false));
    }

    [Fact]
    public void Zero_is_only_sent_when_idle()
    {
        Assert.True(GrblMachineCommands.StateAllowsGCode("Idle"));
        Assert.False(GrblMachineCommands.StateAllowsGCode("Alarm"));
        Assert.False(GrblMachineCommands.StateAllowsGCode("Jog"));
    }

    [Fact]
    public void Go_to_lifts_z_in_machine_coordinates_first()
    {
        var lines = GrblMachineCommands.GoToXY(0, 0, Machine());
        Assert.Equal(new[] { "G53 G0 Z-3", "G90 G0 X0 Y0" }, lines);
        Assert.Equal("G90 G0 X10", GrblMachineCommands.GoToXY(10, null, Machine())[1]);
    }

    [Theory]
    [InlineData("[GC:G0 G54 G17 G21 G90 G94 M5 M9 T0 F0 S0]", "G54")]
    [InlineData("[GC:G1 G56 G17 G21 G90 G94 M3 M9 T0 F500 S12000]", "G56")]
    [InlineData("[MSG:Pgm End]", null)]
    public void Active_wcs_is_read_from_dollar_g(string line, string? expected) =>
        Assert.Equal(expected, GrblMachineCommands.ParseActiveWcs(line));

    [Fact]
    public void Wcs_selection_is_limited_to_g54_to_g59()
    {
        Assert.Equal("G55", GrblMachineCommands.SelectWcs(55));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblMachineCommands.SelectWcs(53));
    }

    [Fact]
    public void Presets_match_the_documented_defaults()
    {
        Assert.Equal(new[] { "Precis", "Normal", "Snabb" }, GrblMachineCommands.DefaultPresets.Select(p => p.Name));
        Assert.All(GrblMachineCommands.DefaultPresets, p => Assert.True(p.StepXY > 0 && p.StepZ > 0 && p.FeedRate > 0));
    }
}
