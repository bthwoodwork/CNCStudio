using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

public class SavedLocationsTests
{
    private static MachineProfile M() => new() { WorkWidthMm = 750, WorkLengthMm = 750, ZTravelMm = 65, LimitMarginMm = 3 };

    [Fact]
    public void Going_to_a_place_lifts_z_first_then_moves_in_machine_coordinates()
    {
        var keepUp = new SavedLocation { Name = "Fräsbyte", X = -375, Y = -700 };
        Assert.Equal(new[] { "G53 G0 Z-3", "G53 G0 X-375 Y-700" }, SavedLocations.GoTo(keepUp, M()));
        var low = new SavedLocation { Name = "Probplatta", X = -10, Y = -10, Z = -40 };
        Assert.Equal(new[] { "G53 G0 Z-3", "G53 G0 X-10 Y-10", "G53 G0 Z-40" }, SavedLocations.GoTo(low, M()));
        Assert.All(SavedLocations.GoTo(low, M()), l => Assert.True(MacroCode.Prepare(l).Ok));
    }

    [Fact]
    public void Places_outside_the_travel_are_refused()
    {
        Assert.Contains("utanför", SavedLocations.Check(new SavedLocation { Name = "a", X = -800, Y = -10 }, M()));
        Assert.Contains("utanför", SavedLocations.Check(new SavedLocation { Name = "a", X = -10, Y = -1 }, M()));    // inside the margin
        Assert.Contains("utanför", SavedLocations.Check(new SavedLocation { Name = "a", X = -10, Y = -10, Z = -70 }, M()));
        Assert.Contains("namn", SavedLocations.Check(new SavedLocation { X = -10, Y = -10 }, M()));
        Assert.Throws<ArgumentException>(() => SavedLocations.GoTo(new SavedLocation { Name = "a", X = 5, Y = -10 }, M()));
    }

    [Fact]
    public void Suggestions_put_the_tool_change_at_the_front_and_are_valid()
    {
        var back = M();
        var s = SavedLocations.Suggestions(back);
        Assert.Equal("Fräsbyte", s[0].Name);
        Assert.True(s[0].Y < -700);
        Assert.True(s[1].Y > -10);
        Assert.All(s, p => Assert.Null(SavedLocations.Check(p, back)));
    }

    [Fact]
    public void Saved_per_machine_and_read_back()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cncstudio-test-" + Guid.NewGuid().ToString("N"));
        var path = SavedLocations.PathFor(folder, "m1");
        var l = new SavedLocations { Locations = { new SavedLocation { Name = "Parkering", X = -375, Y = -5 } } };
        l.Save(path);
        var back = SavedLocations.Load(path);
        Assert.Equal("Parkering", Assert.Single(back.Locations).Name);
        Assert.Null(back.Locations[0].Z);
        Assert.Empty(SavedLocations.Load(Path.Combine(folder, "saknas.json")).Locations);
        Directory.Delete(folder, true);
    }

    [Fact(Timeout = 60000)]
    public async Task Goes_there_on_the_demo_machine()
    {
        var sim = new SimulatedGrbl(speedFactor: 60);
        var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(30))).IsOk);
        var settings = await client.ReadSettingsAsync();
        var m = GrblSettings.ApplyToProfile(M(), settings);
        var p = SavedLocations.Suggestions(m)[0];
        var result = await new JobRunner(client, SavedLocations.GoTo(p, m).ToList(), null).RunAsync();
        Assert.Equal(JobOutcome.Finished, result.Outcome);
        var at = sim.Snapshot().MPos;
        Assert.Equal(p.X, at[0], 2); Assert.Equal(p.Y, at[1], 2);
        client.Dispose();
    }
}
