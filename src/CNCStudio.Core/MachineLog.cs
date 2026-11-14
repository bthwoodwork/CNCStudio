using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNCStudio.Core;

public enum LoggedJobOutcome { Finished, Stopped, Failed }

/// <summary>One program run on the machine (finished, stopped by the user or stopped by an error/alarm).</summary>
public sealed record LoggedJob
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string Name { get; init; } = "";
    public int Lines { get; init; }
    public int LinesRun { get; init; }
    public LoggedJobOutcome Outcome { get; init; }
    public string Message { get; init; } = "";
    public string Computer { get; init; } = "";
    /// <summary>True when the run continued an interrupted job.</summary>
    public bool Resumed { get; init; }
    [JsonIgnore] public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>An alarm the machine reported (ALARM:n), e.g. a soft limit or a hard limit switch.</summary>
public sealed record LoggedAlarm
{
    public DateTime Time { get; init; }
    public int Code { get; init; }
    public string Message { get; init; } = "";
    public string Computer { get; init; } = "";
    /// <summary>The program that was running, if any.</summary>
    public string? Job { get; init; }
}

/// <summary>
/// A recurring maintenance task, due every <see cref="IntervalHours"/> of running time (the time programs
/// have run on this machine), e.g. clean the rails every 20 hours.
/// </summary>
public sealed class MaintenanceTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public double IntervalHours { get; set; } = 20;
    /// <summary>The machine's running hours when it was last done.</summary>
    public double DoneAtHours { get; set; }
    public DateTime? DoneDate { get; set; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Ange vad som ska göras.";
        if (!double.IsFinite(IntervalHours) || IntervalHours <= 0 || IntervalHours > 10000) return "Intervallet måste vara större än 0 och högst 10000 timmar.";
        return null;
    }
}

public enum MaintenanceState { Ok, Soon, Due }

/// <summary>
/// Maskinlogg (like gSender's "Your Machine"): the jobs run, the alarms, running time and maintenance of one
/// machine, in Inställningar\Maskiner\Historik\&lt;id&gt;.json beside the machine's profile – not in it, so projects
/// do not carry the history. Synced like the profiles; each machine is normally run from one computer at a time.
/// </summary>
public sealed class MachineLog
{
    public const string FolderName = "Historik";
    /// <summary>Kept so the file stays small; the totals below keep counting.</summary>
    public const int MaxJobs = 500, MaxAlarms = 500;
    /// <summary>"Soon" from this share of the interval.</summary>
    public const double SoonShare = 0.8;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<LoggedJob> Jobs { get; set; } = new();
    public List<LoggedAlarm> Alarms { get; set; } = new();
    public List<MaintenanceTask> Maintenance { get; set; } = new();

    /// <summary>Running hours from jobs no longer in <see cref="Jobs"/> (dropped to keep the file small).</summary>
    public double OlderHours { get; set; }
    public int OlderFinished { get; set; }
    public int OlderNotFinished { get; set; }

    public DateTime? LastConnected { get; set; }
    public string? Firmware { get; set; }
    public string? LastComputer { get; set; }

    public static string PathFor(string machinesFolder, string machineId) => Path.Combine(machinesFolder, FolderName, machineId + ".json");

    public static MachineLog Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return WithSuggestedMaintenance(new MachineLog());
            var log = JsonSerializer.Deserialize<MachineLog>(File.ReadAllText(path), Json) ?? new MachineLog();
            log.Jobs ??= new(); log.Alarms ??= new(); log.Maintenance ??= new();
            return log;
        }
        catch (Exception) { return new MachineLog(); }   // a damaged file must not stop a run
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AppStorage.WriteAtomic(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    /// A new machine starts with a few suggested tasks for a belt-driven hobby machine – suggestions only,
    /// to be changed or removed after the machine's own manual.
    /// </summary>
    public static MachineLog WithSuggestedMaintenance(MachineLog log)
    {
        log.Maintenance.AddRange(new[]
        {
            new MaintenanceTask { Name = "Rengör skenor och hjul", Description = "Damm och spån bort från skenor, V-hjul och remmar.", IntervalHours = 10 },
            new MaintenanceTask { Name = "Kontrollera remspänning", Description = "Remmarna ska vara spända utan att hoppa vid snabba rörelser.", IntervalHours = 25 },
            new MaintenanceTask { Name = "Kontrollera hjul och glapp", Description = "Excentermuttrarna: hjulen ska rulla utan glapp mot skenan.", IntervalHours = 50 },
            new MaintenanceTask { Name = "Rengör spännhylsan", Description = "Spån i hylsan ger kast och sämre grepp om fräsen.", IntervalHours = 25 },
        });
        return log;
    }

    // ---- Recording ----

    public void AddJob(LoggedJob job)
    {
        Jobs.Add(job);
        while (Jobs.Count > MaxJobs)
        {
            var old = Jobs[0];
            OlderHours += old.Duration.TotalHours;
            if (old.Outcome == LoggedJobOutcome.Finished) OlderFinished++; else OlderNotFinished++;
            Jobs.RemoveAt(0);
        }
    }

    public void AddAlarm(LoggedAlarm alarm)
    {
        Alarms.Add(alarm);
        if (Alarms.Count > MaxAlarms) Alarms.RemoveRange(0, Alarms.Count - MaxAlarms);
    }

    // ---- Statistics ----

    /// <summary>All the time programs have run on the machine, in hours.</summary>
    public double RunningHours => OlderHours + Jobs.Sum(j => j.Duration.TotalHours);
    public int FinishedJobs => OlderFinished + Jobs.Count(j => j.Outcome == LoggedJobOutcome.Finished);
    public int NotFinishedJobs => OlderNotFinished + Jobs.Count(j => j.Outcome != LoggedJobOutcome.Finished);
    public TimeSpan? AverageJob => Jobs.Count == 0 ? null : TimeSpan.FromTicks((long)Jobs.Average(j => j.Duration.Ticks));
    public LoggedJob? LongestJob => Jobs.OrderByDescending(j => j.Duration).FirstOrDefault();

    // ---- Maintenance ----

    /// <summary>Running hours since the task was last done.</summary>
    public double HoursSince(MaintenanceTask t) => Math.Max(0, RunningHours - t.DoneAtHours);

    public MaintenanceState StateOf(MaintenanceTask t)
    {
        double h = HoursSince(t);
        return h >= t.IntervalHours ? MaintenanceState.Due : h >= t.IntervalHours * SoonShare ? MaintenanceState.Soon : MaintenanceState.Ok;
    }

    /// <summary>The tasks that are due now, most overdue first.</summary>
    public IReadOnlyList<MaintenanceTask> DueTasks() =>
        Maintenance.Where(t => StateOf(t) == MaintenanceState.Due).OrderByDescending(t => HoursSince(t) / t.IntervalHours).ToList();

    /// <summary>Marks the task as done now: its hours start again from zero.</summary>
    public void MarkDone(Guid taskId, DateTime when)
    {
        if (Maintenance.FirstOrDefault(t => t.Id == taskId) is { } t) { t.DoneAtHours = RunningHours; t.DoneDate = when; }
    }

    /// <summary>"3 h 25 min", "45 min", "12 s".</summary>
    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{t.Seconds} s";
}
