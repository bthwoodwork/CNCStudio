using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>Probing against the demo machine's simulated touch plate (G38.2, [PRB], G10 L20).</summary>
public class ProbeTests
{
    private const double ToolDia = 6;

    /// <summary>Homed demo machine, tool at machine (-600, -600, -20); plate corner 10 mm out from the tool, top at -30, stock top at -45.</summary>
    private static async Task<(SimulatedGrbl Sim, GrblClient Client)> Machine(bool plate = true)
    {
        var sim = new SimulatedGrbl(speedFactor: 60) { ProbeToolRadius = ToolDia / 2 };
        if (plate) sim.ProbePlate = new SimulatedGrbl.SimProbePlate(-610, -610, -550, -550, -30);
        var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(60))).IsOk);
        Assert.True((await client.SendLineAsync("G53 G0 X-600 Y-600 Z-20")).IsOk);
        Assert.True((await client.SendLineAsync("G4 P0.01", timeout: TimeSpan.FromSeconds(30))).IsOk);
        return (sim, client);
    }

    private static async Task<double[]> G54(GrblClient c) => (await c.ReadParametersAsync())["G54"];

    [Fact]
    public async Task Z_probe_sets_Z0_on_the_stock_under_the_plate()
    {
        var (_, c) = await Machine();
        var s = new ProbeSettings { PlateThicknessMm = 15 };
        var r = await new ProbeCycle(c, s).RunAsync(ProbeKind.Z, ToolDia);
        Assert.True(r.Success, r.Message + "\n" + string.Join("\n", r.Steps));
        Assert.Equal(-45, r.StockTopMPosZ!.Value, 2);          // plate top -30 minus 15 mm
        Assert.Equal(-45, (await G54(c))[2], 2);               // work Z0 = machine Z -45
        var st = await c.RequestStatusAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(-45 + 15 + s.FinalLiftMm, st!.MPos![2], 2); // lifted above the plate afterwards
        c.Dispose();
    }

    [Fact]
    public async Task Corner_probe_sets_X0_Y0_on_the_stock_edges_and_Z0_on_top()
    {
        var (_, c) = await Machine();
        var s = new ProbeSettings { PlateThicknessMm = 15, PlateWallXMm = 10, PlateWallYMm = 8 };
        var r = await new ProbeCycle(c, s).RunAsync(ProbeKind.XYZ, ToolDia);
        Assert.True(r.Success, r.Message + "\n" + string.Join("\n", r.Steps));
        var g54 = await G54(c);
        Assert.Equal(-610 + 10, g54[0], 2);                    // plate's left side + wall
        Assert.Equal(-610 + 8, g54[1], 2);                     // plate's front side + wall
        Assert.Equal(-45, g54[2], 2);
        c.Dispose();
    }

    [Fact]
    public async Task No_plate_under_the_tool_stops_with_a_clear_message()
    {
        var (sim, c) = await Machine(plate: false);
        sim.ProbePlate = new SimulatedGrbl.SimProbePlate(-300, -300, -250, -250, -30);   // somewhere else
        var r = await new ProbeCycle(c, new ProbeSettings()).RunAsync(ProbeKind.Z, ToolDia);
        Assert.False(r.Success);
        Assert.Contains("ALARM:5", r.Message);
        c.Dispose();
    }

    [Fact]
    public async Task A_probe_already_in_contact_is_refused_before_moving()
    {
        var (sim, c) = await Machine();
        sim.ProbePlate = new SimulatedGrbl.SimProbePlate(-610, -610, -550, -550, -15);   // tool at -20 is inside it
        var r = await new ProbeCycle(c, new ProbeSettings()).RunAsync(ProbeKind.Z, ToolDia);
        Assert.False(r.Success);
        Assert.Contains("redan i kontakt", r.Message);
        c.Dispose();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Invalid_settings_are_refused(double feed)
    {
        Assert.NotNull(ProbeValidation.GetError(new ProbeSettings { FastFeed = feed }, ProbeKind.Z, 6));
        Assert.NotNull(ProbeValidation.GetError(new ProbeSettings(), ProbeKind.XY, 0));        // tool diameter needed for X/Y
        Assert.NotNull(ProbeValidation.GetError(new ProbeSettings { SideDepthMm = 20, PlateThicknessMm = 15 }, ProbeKind.XYZ, 6));
        Assert.Null(ProbeValidation.GetError(new ProbeSettings(), ProbeKind.Z, 0));            // Z needs no diameter
    }
}
