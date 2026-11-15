using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

public class FlatnessTests
{
    [Fact]
    public void Points_are_inside_the_area_in_a_short_order()
    {
        var five = Flatness.Points(0, 0, 300, 200, 5);
        Assert.Equal(5, five.Count);
        Assert.All(five, p => { Assert.InRange(p.X, 15, 285); Assert.InRange(p.Y, 15, 185); });
        var nine = Flatness.Points(0, 0, 300, 200, 9);
        Assert.Equal(9, nine.Distinct().Count());
        for (int i = 1; i < nine.Count; i++)
            Assert.True(Math.Abs(nine[i].X - nine[i - 1].X) + Math.Abs(nine[i].Y - nine[i - 1].Y) <= 150 + 1e-9);   // neighbours only
    }

    [Fact]
    public void A_flat_surface_is_ok()
    {
        var r = Flatness.Analyze(new[] { new SurfacePoint(0, 0, -40), new SurfacePoint(100, 0, -40.05), new SurfacePoint(0, 100, -39.98), new SurfacePoint(100, 100, -40.02) });
        Assert.True(r.Ok);
        Assert.StartsWith("Plant", r.Verdict);
    }

    [Fact]
    public void Tilt_and_warp_are_told_apart()
    {
        // Tilted plane: 1 mm over 200 mm in X.
        var tilted = new List<SurfacePoint>();
        foreach (var x in new[] { 0.0, 100, 200 }) foreach (var y in new[] { 0.0, 100, 200 }) tilted.Add(new(x, y, -40 + x / 200));
        var t = Flatness.Analyze(tilted, vBitAngleDeg: 60);
        Assert.False(t.Ok);
        Assert.Equal(0.5, t.TiltPer100Mm, 3);
        Assert.True(t.WarpMm < 1e-6);
        Assert.Contains(t.Advice, a => a.Contains("lutning"));
        Assert.Contains(t.Advice, a => a.Contains("V-carving") && a.Contains("1.15"));     // 2 · 1 · tan 30°

        // Cupped: the middle 0.8 mm lower than the corners.
        var cupped = tilted.Select(p => p with { Z = -40 - (p.X == 100 && p.Y == 100 ? 0.8 : 0) }).ToList();
        var c = Flatness.Analyze(cupped);
        Assert.Contains(c.Advice, a => a.Contains("buktigt"));
    }

    [Fact(Timeout = 60000)]
    public async Task Probes_the_surface_on_the_demo_machine()
    {
        var sim = new SimulatedGrbl(speedFactor: 60);
        var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(30))).IsOk);
        var settings = new ProbeSettings { PlateThicknessMm = 15 };
        var cycle = new ProbeCycle(client, settings);
        var heights = new List<double>();
        foreach (var (x, top) in new[] { (-300.0, -50.0), (-200.0, -50.6) })
        {
            Assert.True((await client.SendLineAsync("G53 G0 Z-3")).IsOk);
            Assert.True((await client.SendLineAsync($"G53 G0 X{x} Y-300")).IsOk);
            Assert.True((await client.SendLineAsync("G53 G0 Z-30")).IsOk);
            sim.ProbePlate = new SimulatedGrbl.SimProbePlate(x - 30, -330, x + 30, -270, top + 15);
            heights.Add(await cycle.ProbeSurfaceHereAsync(40));
        }
        Assert.Equal(-50, heights[0], 2);
        Assert.Equal(-50.6, heights[1], 2);
        client.Dispose();
    }
}
