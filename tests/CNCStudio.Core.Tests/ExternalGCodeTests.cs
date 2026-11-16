using CNCStudio.Core.GCode;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>G-code from other programs: arcs, what GRBL cannot run, the file facts, and Z against the machine.</summary>
public class ExternalGCodeTests
{
    // Like a typical GRBL post-processor output: header comments, %, G2 arcs with I/J, spindle, a lift and a return.
    private const string CamOutput = """
        %
        (1001)
        (T1  D=6 CR=0 - ZMIN=-3 - flat end mill)
        G90 G94
        G17
        G21
        S12000 M3
        G0 X0 Y0
        G0 Z5
        G1 Z-3 F300
        G1 X40 F1000
        G2 X50 Y10 I0 J10
        G1 Y40
        G3 X40 Y50 I-10 J0
        G1 X0
        G0 Z5
        M5
        M30
        %
        """;

    [Fact]
    public void A_typical_file_is_accepted_with_its_facts()
    {
        var p = ExternalGCode.Analyze(CamOutput, "skylt.nc");
        Assert.True(p.Ok, string.Join("\n", p.Problems));
        Assert.DoesNotContain(p.Lines, l => l.StartsWith("(") || l == "%");
        var i = p.Info!;
        Assert.Equal((0.0, 50.0, 0.0, 50.0), (i.MinX, i.MaxX, i.MinY, i.MaxY), new Tol());
        Assert.Equal((-3.0, 5.0), (i.MinZ, i.MaxZ));
        Assert.Equal((300.0, 1000.0), (i.MinFeed!.Value, i.MaxFeed!.Value));
        Assert.Equal(new[] { 12000.0 }, i.SpindleSpeeds);
        Assert.Equal(new[] { "T1" }, i.Tools);
        Assert.True(i.UsesArcs && i.StartsSpindle);
        Assert.True(i.EstimatedMinutes > 0);
        Assert.Contains("Utbredning", ExternalGCode.Summary(i));
    }

    [Theory]
    [InlineData("T2 M6", "M6")]
    [InlineData("G41 D1", "G41")]
    [InlineData("G81 X1 Y1 Z-3 R2 F100", "borrcykel")]
    [InlineData("G1 A90 F100", "A-axeln")]
    [InlineData("$100=80", "Maskininställningar")]
    [InlineData("O1001", "programnummer")]
    [InlineData("M98 P100", "M98")]
    public void What_GRBL_cannot_run_stops_the_file_with_the_line(string bad, string reason)
    {
        var p = ExternalGCode.Analyze("G21 G90\nG0 X0 Y0\n" + bad + "\nM30", "x.nc");
        Assert.False(p.Ok);
        var problem = Assert.Single(p.Problems);
        Assert.StartsWith("Rad 3", problem);
        Assert.Contains(reason, problem);
    }

    [Fact]
    public void Inch_files_are_measured_in_millimetres_and_flagged()
    {
        var p = ExternalGCode.Analyze("G20 G90\nG0 X0 Y0 Z0.2\nG1 Z-0.1 F10\nG1 X2 F20\nM30", "tum.nc");
        Assert.True(p.Ok);
        Assert.Equal(50.8, p.Info!.MaxX, 6);
        Assert.Equal(-2.54, p.Info.MinZ, 6);
        Assert.Contains(p.Warnings, w => w.Contains("tum"));
    }

    [Fact]
    public void An_empty_file_is_refused() => Assert.False(ExternalGCode.Analyze("(bara kommentar)\n%\n", "tom.nc").Ok);

    [Fact]
    public void A_full_circle_with_IJ_reaches_all_round()
    {
        var moves = GCodePath.Parse(new[] { "G0 X10 Y0", "G2 X10 Y0 I-10 J0 F500" });
        Assert.Equal(-10, moves.Min(m => Math.Min(m.X0, m.X1)), 3);
        Assert.Equal(10, moves.Max(m => Math.Max(m.Y0, m.Y1)), 3);
        Assert.Equal(-10, moves.Min(m => Math.Min(m.Y0, m.Y1)), 3);
        Assert.All(moves.Skip(1), m => Assert.True(Math.Sqrt((m.X1 - m.X0) * (m.X1 - m.X0) + (m.Y1 - m.Y0) * (m.Y1 - m.Y0)) <= GCodePath.ArcSegmentMm + 1e-9));
    }

    [Fact]
    public void An_R_arc_bulges_the_way_GRBL_draws_it()
    {
        // G2 (clockwise) from (0,0) to (10,0) with R10: the centre lies right of the travel (below), the arc above.
        var cw = GCodePath.Parse(new[] { "G0 X0 Y0", "G2 X10 Y0 R10 F500" });
        Assert.True(cw.Max(m => m.Y1) > 1.3 && cw.Min(m => m.Y1) > -1e-9);
        // G3 (counter-clockwise) the same way: below.
        var ccw = GCodePath.Parse(new[] { "G0 X0 Y0", "G3 X10 Y0 R10 F500" });
        Assert.True(ccw.Min(m => m.Y1) < -1.3);
    }

    [Fact]
    public void Z_is_checked_against_the_machine_with_the_zero_that_is_set()
    {
        var s = new Dictionary<int, double> { [130] = 500, [131] = 500, [132] = 100 };
        var moves = GCodePath.Parse(new[] { "G0 X10 Y10 Z5", "G1 Z-20 F300", "G0 Z5" });
        Assert.Null(JobEnvelope.Check(moves, new double[] { -400, -400, -45 }, s, "x"));     // Z0 at MPos −45: −65…−40
        var deep = JobEnvelope.Check(moves, new double[] { -400, -400, -90 }, s, "x");        // Z0 too low: −110
        Assert.Contains("Z går ned till MPos -110", deep);
        Assert.Contains("proba Z igen", deep);
        var high = JobEnvelope.Check(moves, new double[] { -400, -400, -2 }, s, "x");         // Z0 near the switch: +3
        Assert.Contains("Z-brytaren", high);
    }

    [Fact(Timeout = 120000)]
    public async Task The_file_runs_on_the_simulated_machine_after_the_checks()
    {
        var sim = new SimulatedGrbl(speedFactor: 60);
        using var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(30))).IsOk);
        // Zero X/Y inside the table and Z 40 mm below the switch, as on a real setup.
        foreach (var l in new[] { "G53 G0 X-300 Y-300", "G53 G0 Z-40", "G10 L20 P0 X0 Y0 Z0" })
            Assert.True((await client.SendLineAsync(l, timeout: TimeSpan.FromSeconds(30))).IsOk, l);
        var p = ExternalGCode.Analyze(CamOutput, "skylt.nc");
        var settings = await client.ReadSettingsAsync();
        var g54 = (await client.ReadParametersAsync())["G54"];
        Assert.Null(JobEnvelope.Check(GCodePath.Parse(p.Lines), g54, settings, "x"));
        var result = await new JobRunner(client, p.Lines, null).RunAsync();
        Assert.True(result.Outcome == JobOutcome.Finished, result.Message);
    }

    private sealed class Tol : IEqualityComparer<(double, double, double, double)>
    {
        public bool Equals((double, double, double, double) a, (double, double, double, double) b) =>
            Math.Abs(a.Item1 - b.Item1) < 1e-6 && Math.Abs(a.Item2 - b.Item2) < 1e-6 && Math.Abs(a.Item3 - b.Item3) < 1e-6 && Math.Abs(a.Item4 - b.Item4) < 1e-6;
        public int GetHashCode((double, double, double, double) obj) => 0;
    }
}
