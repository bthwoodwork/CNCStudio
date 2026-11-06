using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

public class CommissioningTests
{
    private static Dictionary<int, double> Settings() => SimulatedGrbl.DefaultSettings.ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public void Demo_machine_settings_pass_with_the_default_profile()
    {
        var checks = Commissioning.ReviewSettings(Settings(), new MachineProfile());
        Assert.DoesNotContain(checks, c => c.Level == CheckLevel.Stop);
    }

    [Theory]
    [InlineData(13, 1)]   // inches
    [InlineData(32, 1)]   // laser mode
    [InlineData(22, 0)]   // homing off
    [InlineData(23, 7)]   // Z homes down
    [InlineData(130, 0)]  // no travel
    public void Dangerous_settings_stop_the_test(int setting, double value)
    {
        var s = Settings(); s[setting] = value;
        Assert.Contains(Commissioning.ReviewSettings(s, new MachineProfile()), c => c.Level == CheckLevel.Stop);
    }

    [Fact]
    public void Profile_with_another_switch_corner_is_flagged()
    {
        var checks = Commissioning.ReviewSettings(Settings(), new MachineProfile { HomingDirInvertMask = 0 });
        Assert.Contains(checks, c => c.Level == CheckLevel.Warning && c.Text.Contains("$23"));
    }

    [Fact]
    public void Flipping_a_direction_toggles_only_that_axis()
    {
        Assert.Equal(1, Commissioning.FlipDirection(0, 0));
        Assert.Equal(5, Commissioning.FlipDirection(7, 1));
        Assert.Equal(3, Commissioning.FlipDirection(7, 2));
    }

    [Fact]
    public void Expected_home_position_follows_limits_c()
    {
        var home = Commissioning.ExpectedHomePosition(Settings());     // $23=3, $27=1, 750/750/65
        Assert.Equal(new[] { -749.0, -749.0, -1.0 }, home);
    }

    [Fact]
    public async Task Air_square_runs_inward_from_the_switch_corner_and_returns()
    {
        var sim = new SimulatedGrbl(speedFactor: 40);
        var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(30))).IsOk);
        var start = sim.Snapshot().MPos;
        var lines = Commissioning.AirSquare(Settings(), 20, 600, 3);
        Assert.Contains("G91", lines);
        Assert.Equal("G90", lines[^1]);
        var result = await new JobRunner(client, lines, null).RunAsync();
        Assert.Equal(JobOutcome.Finished, result.Outcome);
        var end = sim.Snapshot().MPos;
        Assert.Equal(start[0], end[0], 3);
        Assert.Equal(start[1], end[1], 3);
        Assert.Equal(-3, end[2], 3);                                    // G53 Z just below the switch
        client.Dispose();
    }
}
