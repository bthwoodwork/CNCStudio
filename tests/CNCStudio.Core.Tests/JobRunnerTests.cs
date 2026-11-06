using System.Globalization;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

public class JobRunnerTests
{
    /// <summary>A small program like CNC Studio writes: two square contours with travels between them.</summary>
    private static List<string> Program(bool manual = false)
    {
        var l = new List<string> { "(test)", "G21", "G90", "G94", "G17", "G54", "G0 Z10" };
        if (manual) { l.Add("(PAUS: starta frasen for hand, stall 18000 varv/min, fortsatt med ~)"); l.Add("M0"); }
        else { l.Add("M3 S18000"); l.Add("G4 P0.1"); }
        foreach (var (ox, oy) in new[] { (20.0, 20.0), (120.0, 20.0), (20.0, 120.0) })
        {
            l.Add($"G0 X{ox} Y{oy}"); l.Add("G0 Z2"); l.Add("G1 Z-1 F300");
            for (int k = 0; k < 8; k++)
            {
                double s = 10 + k;
                l.Add(string.Create(CultureInfo.InvariantCulture, $"G1 X{ox + s} Y{oy} F1500"));
                l.Add(string.Create(CultureInfo.InvariantCulture, $"G1 X{ox + s} Y{oy + s}"));
                l.Add(string.Create(CultureInfo.InvariantCulture, $"G1 X{ox} Y{oy + s}"));
                l.Add($"G1 X{ox} Y{oy}");
            }
            l.Add("G0 Z10");
        }
        if (!manual) l.Add("M5");
        l.Add("M30");
        return l;
    }

    private static async Task<(SimulatedGrbl Sim, GrblClient Client)> HomedMachine(SimulatedGrbl? sim = null)
    {
        sim ??= new SimulatedGrbl(speedFactor: 60);
        var client = new GrblClient(sim);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        Assert.True((await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(30))).IsOk);
        return (sim, client);
    }

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "cncstudio-test-" + Guid.NewGuid().ToString("N"));

    [Fact(Timeout = 60000)]
    public async Task Runs_a_program_to_the_end_and_records_it_as_finished()
    {
        var (_, client) = await HomedMachine();
        Assert.True((await client.SendLineAsync("G10 L20 P0 X0 Y0 Z60")).IsOk);
        var lines = Program();
        var store = new JobProgressStore(TempFolder());
        store.Begin("test", lines, 0, null);
        var runner = new JobRunner(client, lines, store);
        var run = runner.RunAsync();
        var finished = await Task.WhenAny(run, Task.Delay(20000));
        Assert.True(finished == run, $"hängde på rad {runner.AckedLines}: {runner.CurrentLine}");
        var result = await run;
        Assert.True(result.Outcome == JobOutcome.Finished, result.Message);
        Assert.Equal(lines.Count, store.Load()!.AckedLines);
        Assert.Equal("Finished", store.Load()!.State);
        client.Dispose();
    }

    [Fact]
    public void Resume_point_is_a_cut_point_at_least_a_planner_buffer_before_the_last_ok()
    {
        var lines = Program();
        var plan = ResumePlanner.Plan(lines, 80, new MachineProfile { Spindle = SpindleControl.Grbl },
            new MotionSettings { SpindleSpinUpSeconds = 2, PlungeClearanceMm = 2 });
        Assert.False(plan.FromBeginning);
        int resumedLine = plan.StartIndex - 1;                               // re-sent in the preamble
        Assert.True(resumedLine <= 80 - ResumePlanner.PlannerBlocks - 1);   // certainly already executed
        Assert.StartsWith("G1 X", lines[resumedLine]);
        Assert.Equal(new[] { "G21", "G90", "G94", "G17", "G54", "G53 G0 Z-3" }, plan.Preamble.Take(6));
        Assert.Contains("M3 S18000", plan.Preamble);
        Assert.Equal("G0 Z2", plan.Preamble[^3]);                            // plunge clearance
        Assert.Equal("G1 Z-1 F300", plan.Preamble[^2]);                      // down through the cut slot at plunge feed
        Assert.EndsWith("F1500", plan.Preamble[^1]);                         // the cutting line with its feed restored
    }

    [Fact]
    public void Resume_at_a_travel_move_continues_at_travel_height()
    {
        var lines = Program();
        int travel = lines.IndexOf("G0 X120 Y20");
        // Last ok so that the travel move is the last certainly executed line.
        var plan = ResumePlanner.Plan(lines, travel + ResumePlanner.PlannerBlocks + 1, new MachineProfile { Spindle = SpindleControl.Grbl }, new MotionSettings());
        Assert.Equal(travel + 1, plan.StartIndex);
        Assert.Equal("G0 Z10 F1500", plan.Preamble[^1]);
    }
    [Fact]
    public void Manual_spindle_resume_pauses_with_M0_and_says_the_speed()
    {
        var plan = ResumePlanner.Plan(Program(manual: true), 70, new MachineProfile { Spindle = SpindleControl.Manual }, new MotionSettings());
        Assert.Contains("M0", plan.Preamble);
        Assert.Contains(plan.Preamble, l => l.Contains("18000 varv/min"));
        Assert.DoesNotContain(plan.Preamble, l => l.StartsWith("M3"));
    }

    [Fact]
    public void Too_early_interruption_restarts_from_the_beginning()
    {
        var plan = ResumePlanner.Plan(Program(), 12, new MachineProfile(), new MotionSettings());
        Assert.True(plan.FromBeginning);
        Assert.Equal(0, plan.StartIndex);
    }

    [Fact(Timeout = 60000)]
    public async Task Power_cut_mid_job_is_saved_and_the_job_can_be_resumed_after_homing()
    {
        var folder = TempFolder();
        var (sim, client) = await HomedMachine(new SimulatedGrbl(speedFactor: 8));
        Assert.True((await client.SendLineAsync("G10 L20 P0 X0 Y0 Z60")).IsOk);
        var lines = Program();
        var store = new JobProgressStore(folder);
        store.Begin("test", lines, 0, new double[] { 0, 0, 0 });
        var runner = new JobRunner(client, lines, store);
        var run = runner.RunAsync();
        var waitUntil = DateTime.UtcNow.AddSeconds(30);
        while (runner.AckedLines < 70 && runner.IsRunning && DateTime.UtcNow < waitUntil) await Task.Delay(10);
        Assert.True(runner.IsRunning, "Jobbet tog slut innan strömavbrottet");
        sim.PowerCycle();                                                    // power cut: board restarts
        var result = await run;
        Assert.Equal(JobOutcome.Failed, result.Outcome);
        client.Dispose();

        // After the restart: the saved job is found, the machine is homed, and the job continues.
        var saved = new JobProgressStore(folder).LoadJob();
        Assert.NotNull(saved);
        Assert.True(saved!.Value.Progress.Unfinished);
        Assert.True(saved.Value.Progress.AckedLines >= 70);
        var (_, client2) = await HomedMachine(sim);
        var g54 = (await client2.ReadParametersAsync())["G54"];
        Assert.Equal(-749, g54[0], 1);                                       // G54 survived (EEPROM)
        var plan = ResumePlanner.Plan(saved.Value.Lines, saved.Value.Progress.AckedLines,
            new MachineProfile { Spindle = SpindleControl.Grbl }, new MotionSettings());
        var store2 = new JobProgressStore(folder);
        store2.Begin("test", saved.Value.Lines, plan.StartIndex, g54);
        var resumed = await new JobRunner(client2, saved.Value.Lines, store2).RunAsync(plan.StartIndex, plan.Preamble);
        Assert.Equal(JobOutcome.Finished, resumed.Outcome);
        client2.Dispose();
    }

    [Fact(Timeout = 60000)]
    public async Task Check_mode_finds_a_bad_line_and_moves_nothing()
    {
        var (sim, client) = await HomedMachine();
        var before = sim.Snapshot().MPos;
        var lines = new List<string> { "G21", "G90", "G1 X-10 F500", "G5 X1", "G0 X-20" };
        var errors = await JobRunner.CheckAsync(client, lines);
        Assert.Single(errors);
        Assert.Contains("Rad 4", errors[0]);
        await Task.Delay(100);
        Assert.Equal(before, sim.Snapshot().MPos);
        client.Dispose();
    }

    [Fact(Timeout = 60000)]
    public async Task Stop_holds_then_resets_and_keeps_the_position()
    {
        var (sim, client) = await HomedMachine(new SimulatedGrbl(speedFactor: 30));
        var lines = new List<string> { "G21", "G90", "G1 X-100 F300" };
        var runner = new JobRunner(client, lines, null);
        var run = runner.RunAsync();
        await Task.Delay(400);
        await runner.StopAsync();
        var result = await run;
        Assert.Equal(JobOutcome.Stopped, result.Outcome);
        await Task.Delay(100);
        var s = await client.RequestStatusAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("Idle", s!.State);                                      // no alarm: reset while not moving
        Assert.True(sim.Snapshot().Homed);
        client.Dispose();
    }
}
