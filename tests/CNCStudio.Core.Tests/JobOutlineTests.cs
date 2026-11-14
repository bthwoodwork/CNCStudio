using CNCStudio.Core.GCode;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>Rita konturen: the outline of what a program cuts, followed high above the material.</summary>
public class JobOutlineTests
{
    // A circle of radius 20 around (30, 30), cut at Z-2, with a rapid far outside it that must not count.
    private static IReadOnlyList<ProgramMove> Circle() => GCodePath.Parse(new[]
    {
        "G21 G90", "G0 Z5", "G0 X200 Y200", "G0 X50 Y30", "G1 Z-2 F300", "G2 X50 Y30 I-20 J0 F1000", "G0 Z5", "G0 X0 Y0"
    });

    [Fact]
    public void The_rectangle_is_what_is_cut_not_where_rapids_go()
    {
        var pts = JobOutline.Points(Circle(), OutlineShape.Rectangle);
        Assert.Equal(4, pts.Count);
        Assert.Equal(10, pts.Min(p => p.X), 3); Assert.Equal(50, pts.Max(p => p.X), 3);
        Assert.Equal(10, pts.Min(p => p.Y), 3); Assert.Equal(50, pts.Max(p => p.Y), 3);
    }

    [Fact]
    public void The_hull_follows_the_round_shape_inside_the_rectangle()
    {
        var hull = JobOutline.Points(Circle(), OutlineShape.Hull);
        Assert.True(hull.Count > 20);
        Assert.All(hull, p => Assert.Equal(20, Math.Sqrt((p.X - 30) * (p.X - 30) + (p.Y - 30) * (p.Y - 30)), 2));
    }

    [Fact]
    public void The_lines_lift_first_go_round_and_come_back_without_going_down()
    {
        var pts = JobOutline.Points(Circle(), OutlineShape.Rectangle);
        var lines = JobOutline.Lines(pts, safeMachineZ: -3, feed: 2000, returnTo: (5, 5));
        Assert.Equal("G53 G0 Z-3", lines[0]);
        Assert.Equal("G90 G0 X10 Y10", lines[1]);
        Assert.Equal("G1 X10 Y10 F2000", lines[^2]);                     // back at the first corner
        Assert.Equal("G0 X5 Y5", lines[^1]);
        Assert.DoesNotContain(lines.Skip(1), l => l.Contains('Z'));         // only the lift touches Z
        Assert.True(MacroCode.Prepare(string.Join("\n", lines)).Ok);
    }

    [Fact]
    public void A_program_that_cuts_nothing_has_no_outline()
    {
        Assert.Empty(JobOutline.Points(GCodePath.Parse(new[] { "G0 X10 Y10", "G0 X0 Y0" }), OutlineShape.Hull));
        Assert.Throws<ArgumentException>(() => JobOutline.Lines(Array.Empty<(double, double)>(), -3, 1000, null));
    }

    [Fact(Timeout = 60000)]
    public async Task The_outline_runs_on_the_simulated_machine()
    {
        var sim = new SimulatedGrbl(speedFactor: 60);
        using var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(30))).IsOk);
        foreach (var l in new[] { "G53 G0 X-300 Y-300", "G10 L20 P0 X0 Y0" })
            Assert.True((await client.SendLineAsync(l, timeout: TimeSpan.FromSeconds(30))).IsOk);
        var lines = JobOutline.Lines(JobOutline.Points(Circle(), OutlineShape.Hull), -3, 3000, (0, 0));
        var result = await new JobRunner(client, lines, null).RunAsync();
        Assert.True(result.Outcome == JobOutcome.Finished, result.Message);
    }
}
