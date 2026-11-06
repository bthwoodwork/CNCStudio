using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

public class SimulatedGrblTests
{
    [Fact]
    public async Task Demo_machine_starts_locked_homes_to_minus_pulloff_and_jogs()
    {
        var sim = new SimulatedGrbl(speedFactor: 40);
        var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("Alarm", (await client.RequestStatusAsync(TimeSpan.FromSeconds(1)))!.State);
        Assert.Equal(9, (await client.SendLineAsync("G0 X-10")).Completion.Code);       // G-code lock

        var log = new List<string>();
        client.LineReceived += l => log.Add(l.Raw);
        GrblResponse home;
        try { home = await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            var snap = sim.Snapshot();
            throw new Exception($"state {snap.State} phys {string.Join(",", snap.Physical)} pos {string.Join(",", snap.MPos)} q {snap.QueuedBlocks} log {string.Join("|", log)}");
        }
        Assert.True(home.IsOk, home.Describe());
        var s = await client.RequestStatusAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("Idle", s!.State);
        // X/Y home toward minus ($23=3): MPos = −$13x + $27; Z toward plus: −$27 (limits.c).
        Assert.Equal(new[] { -749.0, -749.0, -1.0 }, s.MPos!.Select(v => Math.Round(v, 3)));

        Assert.Equal(15, (await client.SendLineAsync("$J=G91 G21 Z5 F500")).Completion.Code); // above Z 0
        Assert.True((await client.SendLineAsync("$J=G91 G21 X20 F6000")).IsOk);
        await Task.Delay(300);
        Assert.Equal(-729, (await client.RequestStatusAsync(TimeSpan.FromSeconds(1)))!.MPos![0], 2);

        Assert.True((await client.SendLineAsync("G10 L20 P0 X0 Y0")).IsOk);
        var p = await client.ReadParametersAsync();
        Assert.Equal(-729, p["G54"][0], 2);
        client.Dispose();
    }
}
