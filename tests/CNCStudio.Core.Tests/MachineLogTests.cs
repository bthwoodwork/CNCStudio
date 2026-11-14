namespace CNCStudio.Core.Tests;

/// <summary>Maskinlogg: jobs, alarms, running time and maintenance per machine (like gSender's Your Machine).</summary>
public class MachineLogTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "cncstudio-log-" + Guid.NewGuid().ToString("N"), "x.json");

    private static LoggedJob Job(double minutes, LoggedJobOutcome outcome = LoggedJobOutcome.Finished, string name = "Skylt") =>
        new() { Start = new DateTime(2026, 11, 14, 10, 0, 0), End = new DateTime(2026, 11, 14, 10, 0, 0).AddMinutes(minutes), Name = name, Outcome = outcome };

    [Fact]
    public void A_new_machine_gets_suggested_maintenance_and_the_log_is_saved_and_read_back()
    {
        var path = Temp();
        var log = MachineLog.Load(path);
        Assert.NotEmpty(log.Maintenance);
        log.AddJob(Job(90));
        log.AddAlarm(new LoggedAlarm { Time = DateTime.Now, Code = 2, Message = "Soft limit", Job = "Planfräsa" });
        log.Firmware = "Grbl 1.1h";
        log.Save(path);
        var back = MachineLog.Load(path);
        Assert.Single(back.Jobs);
        Assert.Equal(90, back.Jobs[0].Duration.TotalMinutes, 6);
        Assert.Equal(2, back.Alarms.Single().Code);
        Assert.Equal("Grbl 1.1h", back.Firmware);
        Assert.Equal(log.Maintenance.Count, back.Maintenance.Count);
    }

    [Fact]
    public void Statistics_count_finished_and_unfinished_jobs_and_the_running_time()
    {
        var log = new MachineLog();
        log.AddJob(Job(60)); log.AddJob(Job(30, LoggedJobOutcome.Failed)); log.AddJob(Job(120, LoggedJobOutcome.Stopped, "Lång"));
        Assert.Equal(3.5, log.RunningHours, 6);
        Assert.Equal((1, 2), (log.FinishedJobs, log.NotFinishedJobs));
        Assert.Equal(70, log.AverageJob!.Value.TotalMinutes, 6);
        Assert.Equal("Lång", log.LongestJob!.Name);
        Assert.Equal("3 h 30 min", MachineLog.Format(TimeSpan.FromHours(3.5)));
    }

    [Fact]
    public void Old_jobs_are_dropped_but_still_count()
    {
        var log = new MachineLog();
        for (int i = 0; i < MachineLog.MaxJobs + 10; i++) log.AddJob(Job(6));
        Assert.Equal(MachineLog.MaxJobs, log.Jobs.Count);
        Assert.Equal((MachineLog.MaxJobs + 10) * 0.1, log.RunningHours, 6);
        Assert.Equal(MachineLog.MaxJobs + 10, log.FinishedJobs);
    }

    [Fact]
    public void Maintenance_becomes_due_with_running_time_and_starts_over_when_done()
    {
        var log = new MachineLog();
        var task = new MaintenanceTask { Name = "Rengör skenor", IntervalHours = 10 };
        log.Maintenance.Add(task);
        log.AddJob(Job(7 * 60));
        Assert.Equal(MaintenanceState.Ok, log.StateOf(task));
        log.AddJob(Job(60 + 30));                       // 8.5 h: from 80 % it is "soon"
        Assert.Equal(MaintenanceState.Soon, log.StateOf(task));
        log.AddJob(Job(2 * 60));                        // 10.5 h
        Assert.Equal(MaintenanceState.Due, log.StateOf(task));
        Assert.Same(task, Assert.Single(log.DueTasks()));
        log.MarkDone(task.Id, DateTime.Now);
        Assert.Equal(0, log.HoursSince(task), 6);
        Assert.Equal(MaintenanceState.Ok, log.StateOf(task));
        Assert.Empty(log.DueTasks());
    }

    [Fact]
    public void A_damaged_file_gives_an_empty_log_instead_of_stopping_the_run()
    {
        var path = Temp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ inte json");
        var log = MachineLog.Load(path);
        Assert.Empty(log.Jobs);
    }

    [Fact]
    public void The_log_lies_beside_the_profiles_and_is_not_taken_for_a_machine()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cncstudio-lib-" + Guid.NewGuid().ToString("N"))).FullName;
        var lib = new MachineLibrary(folder);
        var m = lib.CreateNew("X-Carve", new MachineProfile());
        var log = MachineLog.Load(lib.MachineLogPath(m.Id!));
        log.AddJob(Job(10));
        log.Save(lib.MachineLogPath(m.Id!));
        Assert.Single(lib.All(), x => x.Name == "X-Carve");
        Assert.True(File.Exists(lib.MachineLogPath(m.Id!)));
        lib.CreateNew("Shapeoko", new MachineProfile());      // so X-Carve is not the last machine
        Assert.Null(lib.Delete(m.Id!));
        Assert.False(File.Exists(lib.MachineLogPath(m.Id!)));  // the log goes with the machine
    }

    [Fact]
    public void Tasks_need_a_name_and_a_sensible_interval()
    {
        Assert.NotNull(new MaintenanceTask { Name = "", IntervalHours = 10 }.Validate());
        Assert.NotNull(new MaintenanceTask { Name = "x", IntervalHours = 0 }.Validate());
        Assert.Null(new MaintenanceTask { Name = "x", IntervalHours = 10 }.Validate());
    }
}
