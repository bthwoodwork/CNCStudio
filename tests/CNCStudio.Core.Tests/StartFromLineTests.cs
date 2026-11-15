using System.Globalization;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

public class StartFromLineTests
{
    /// <summary>Three square contours with rapids at travel height between them (like CNC Studio writes).</summary>
    private static List<string> Program(bool inches = false, string wcs = "G54")
    {
        var l = new List<string> { "(test)", inches ? "G20" : "G21", "G90", "G94", "G17", wcs, "G0 Z10", "M3 S18000", "G4 P0.1" };
        foreach (var (ox, oy) in new[] { (20.0, 20.0), (120.0, 20.0), (20.0, 120.0) })
        {
            l.Add($"(Kontur vid {ox} {oy})");
            l.Add(string.Create(CultureInfo.InvariantCulture, $"G0 X{ox} Y{oy}")); l.Add("G0 Z2"); l.Add("G1 Z-1 F300");
            l.Add(string.Create(CultureInfo.InvariantCulture, $"G1 X{ox + 10} Y{oy} F1500"));
            l.Add(string.Create(CultureInfo.InvariantCulture, $"G1 X{ox + 10} Y{oy + 10}"));
            l.Add(string.Create(CultureInfo.InvariantCulture, $"G1 X{ox} Y{oy + 10}"));
            l.Add(string.Create(CultureInfo.InvariantCulture, $"G1 X{ox} Y{oy}"));
            l.Add("G0 Z10");
        }
        l.Add("M5"); l.Add("M30");
        return l;
    }

    [Fact]
    public void Starting_at_a_contour_header_begins_right_after_the_rapid_there()
    {
        var lines = Program();
        int header = lines.IndexOf("(Kontur vid 120 20)");
        // The rapid to (120, 20) comes right after the header; choosing the line after it starts there.
        var plan = ResumePlanner.PlanFromLine(lines, header + 2, new MachineProfile { Spindle = SpindleControl.Grbl }, new MotionSettings());
        Assert.False(plan.FromBeginning);
        Assert.Equal(header + 2, plan.StartIndex);
        Assert.Equal((120.0, 20.0), (plan.X, plan.Y));
        Assert.Contains(plan.Preamble, l => l.StartsWith("G53 G0 Z"));
        Assert.Contains("M3 S18000", plan.Preamble);
        Assert.Contains($"rad {header + 3}", plan.Description);
    }

    [Fact]
    public void A_line_in_the_middle_of_a_cut_starts_at_a_safe_point_before_it()
    {
        var lines = Program();
        int cut = lines.IndexOf("G1 X130 Y30");          // second contour, mid-square
        var plan = ResumePlanner.PlanFromLine(lines, cut, new MachineProfile { Spindle = SpindleControl.Grbl }, new MotionSettings());
        Assert.False(plan.FromBeginning);
        Assert.True(plan.StartIndex <= cut);
        Assert.True(plan.StartIndex > lines.IndexOf("(Kontur vid 120 20)"));
        // Never starts with the tool going down in an uncut spot: the way in ends at travel height or in the cut slot.
        Assert.DoesNotContain(plan.Preamble, l => l.StartsWith("G0 Z-"));
    }

    [Fact]
    public void The_first_lines_or_line_zero_start_from_the_beginning()
    {
        var lines = Program();
        Assert.True(ResumePlanner.PlanFromLine(lines, 0, new MachineProfile { Spindle = SpindleControl.Grbl }, new MotionSettings()).FromBeginning);
        Assert.True(ResumePlanner.PlanFromLine(lines, 3, new MachineProfile { Spindle = SpindleControl.Grbl }, new MotionSettings()).FromBeginning);
    }

    [Fact]
    public void Keeps_the_programs_units_and_coordinate_system()
    {
        var lines = Program(inches: true, wcs: "G55");
        int header = lines.IndexOf("(Kontur vid 120 20)");
        var plan = ResumePlanner.PlanFromLine(lines, header + 2, new MachineProfile { Spindle = SpindleControl.Grbl }, new MotionSettings());
        int g53 = plan.Preamble.ToList().FindIndex(l => l.StartsWith("G53"));
        int g20 = plan.Preamble.ToList().IndexOf("G20");
        Assert.Contains("G55", plan.Preamble);
        Assert.True(g53 >= 0 && g20 > g53, "G53 Z in mm first, then the program's inches");
    }

    [Fact(Timeout = 60000)]
    public async Task Runs_from_the_chosen_line_on_the_demo_machine()
    {
        var sim = new SimulatedGrbl(speedFactor: 60);
        var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(30))).IsOk);
        Assert.True((await client.SendLineAsync("G53 G0 X-300 Y-300")).IsOk);
        Assert.True((await client.SendLineAsync("G10 L20 P1 X0 Y0 Z20")).IsOk);
        var lines = Program();
        int header = lines.IndexOf("(Kontur vid 20 120)");
        var plan = ResumePlanner.PlanFromLine(lines, header + 2, new MachineProfile { Spindle = SpindleControl.Grbl }, new MotionSettings());
        var result = await new JobRunner(client, lines, null).RunAsync(plan.StartIndex, plan.Preamble);
        Assert.Equal(JobOutcome.Finished, result.Outcome);
        client.Dispose();
    }
}
