using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>The demo machine keeps its settings and work offsets between program starts, and its frame follows $130–$132.</summary>
public class DemoMachineStateTests
{
    [Fact]
    public async Task Settings_and_offsets_survive_a_restart_and_the_frame_follows_the_travel()
    {
        var file = Path.Combine(Path.GetTempPath(), "cncstudio-demo-" + Guid.NewGuid().ToString("N") + ".json");
        var first = new SimulatedGrbl(speedFactor: 60, stateFile: file);
        using (var c = new GrblClient(first))
        {
            await c.ConnectAsync(TimeSpan.FromSeconds(1));
            Assert.True((await c.WriteSettingsAsync(new[] { (130, 500.0), (131, 500.0), (132, 100.0) })).Success);
            Assert.True((await c.SendLineAsync("$X")).IsOk);
            Assert.True((await c.SendLineAsync("G10 L2 P1 X12 Y34 Z-5")).IsOk);
        }

        var second = new SimulatedGrbl(speedFactor: 60, stateFile: file);    // "the program was restarted"
        using var c2 = new GrblClient(second);
        await c2.ConnectAsync(TimeSpan.FromSeconds(1));
        var s = await c2.ReadSettingsAsync();
        Assert.Equal((500.0, 500.0, 100.0), (s[130], s[131], s[132]));
        Assert.Equal(new[] { 12.0, 34.0, -5.0 }, (await c2.ReadParametersAsync())["G54"]);
        Assert.True((await c2.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(60))).IsOk);
        var st = await c2.RequestStatusAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(-499, st!.MPos![0], 3);                                 // homed at a 500 mm frame (−$130 + $27)
        Assert.Equal(new[] { (500.0, 500.0, 100.0) }, new[] { (second.Snapshot().PhysicalTravel[0], second.Snapshot().PhysicalTravel[1], second.Snapshot().PhysicalTravel[2]) });
    }

    [Fact]
    public async Task The_profiles_work_area_can_be_written_to_the_demo_machine_and_survives_a_restart()
    {
        var file = Path.Combine(Path.GetTempPath(), "cncstudio-demo-" + Guid.NewGuid().ToString("N") + ".json");
        var profile = new CNCStudio.Core.MachineProfile { WorkWidthMm = 500, WorkLengthMm = 500, ZTravelMm = 100 };
        using (var c = new GrblClient(new SimulatedGrbl(speedFactor: 60, stateFile: file)))
        {
            await c.ConnectAsync(TimeSpan.FromSeconds(1));
            var writes = GrblSettings.TravelWritesFromProfile(profile, await c.ReadSettingsAsync());
            Assert.Contains((130, 500.0), writes);                               // the demo starts as a 750 × 750 X-Carve
            Assert.Contains((131, 500.0), writes);
            Assert.True((await c.WriteSettingsAsync(writes)).Success);
        }
        using var c2 = new GrblClient(new SimulatedGrbl(speedFactor: 60, stateFile: file));   // "the program was restarted"
        await c2.ConnectAsync(TimeSpan.FromSeconds(1));
        var s = await c2.ReadSettingsAsync();
        Assert.Equal((500.0, 500.0, 100.0), (s[130], s[131], s[132]));
        Assert.Empty(GrblSettings.TravelWritesFromProfile(profile, s));          // nothing left to offer at the next connect
    }

    [Fact]
    public void Only_travel_that_differs_is_written()
    {
        var profile = new CNCStudio.Core.MachineProfile { WorkWidthMm = 500, WorkLengthMm = 500, ZTravelMm = 65, HomingDirInvertMask = 0 };
        var machine = new Dictionary<int, double> { [23] = 3, [130] = 750, [131] = 500, [132] = 65 };
        Assert.Equal(new[] { (130, 500.0) }, GrblSettings.TravelWritesFromProfile(profile, machine));   // $23 is never written from here
    }

    [Fact]
    public void Without_a_file_it_is_an_XCarve_750_as_before()
    {
        var sim = new SimulatedGrbl();
        Assert.Equal(750, sim.Settings[130]);
        Assert.Equal(750, sim.Snapshot().PhysicalTravel[0]);
    }
}
