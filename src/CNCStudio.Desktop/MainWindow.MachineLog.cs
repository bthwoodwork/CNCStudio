using System.Windows;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>Maskinlogg: what is recorded per machine (jobs, alarms, connections) and the window that shows it.</summary>
public partial class MainWindow
{
    /// <summary>The connected machine's log: loaded, changed and saved in one go. Never stops what the user is doing.</summary>
    private void UpdateMachineLog(Action<MachineLog> change, string? machineId = null)
    {
        var id = machineId ?? _document.Machine.Id;
        if (id is null) return;
        try
        {
            var path = AppStorage.Current.Machines.MachineLogPath(id);
            var log = MachineLog.Load(path);
            change(log);
            log.Save(path);
        }
        catch (Exception ex) { App.LogError(new InvalidOperationException("Maskinloggen kunde inte sparas.", ex)); }
    }

    private void LogConnected(string? firmware) => UpdateMachineLog(log =>
    {
        log.LastConnected = DateTime.Now; log.Firmware = firmware; log.LastComputer = AppStorage.ComputerName;
    });

    private void LogAlarm(GrblLine line)
    {
        if (line.Code is not int code) return;
        string message = GrblSettings.Alarms.TryGetValue(code, out var m) ? m : "okänt larm";
        string? job = _runner is not null ? _jobName : null;
        UpdateMachineLog(log => log.AddAlarm(new LoggedAlarm { Time = DateTime.Now, Code = code, Message = message, Computer = AppStorage.ComputerName, Job = job }));
    }

    /// <summary>Records a run; returns the maintenance that is due afterwards (to mention when the job is done).</summary>
    private IReadOnlyList<MaintenanceTask> LogJob(DateTime start, JobResult result, int totalLines, bool resumed)
    {
        IReadOnlyList<MaintenanceTask> due = Array.Empty<MaintenanceTask>();
        UpdateMachineLog(log =>
        {
            log.AddJob(new LoggedJob
            {
                Start = start, End = DateTime.Now, Name = _jobName.Length > 0 ? _jobName : _document.Name, Lines = totalLines, LinesRun = result.AckedLines,
                Outcome = result.Outcome switch { JobOutcome.Finished => LoggedJobOutcome.Finished, JobOutcome.Stopped => LoggedJobOutcome.Stopped, _ => LoggedJobOutcome.Failed },
                Message = result.Message, Computer = AppStorage.ComputerName, Resumed = resumed
            });
            due = log.DueTasks();
        });
        return due;
    }

    /// <summary>"UNDERHÅLL: …" lines for the job-done dialog, or "".</summary>
    private static string DueText(IReadOnlyList<MaintenanceTask> due) =>
        due.Count == 0 ? "" : "\n\nUNDERHÅLL ATT GÖRA:\n" + string.Join("\n", due.Select(t => "• " + t.Name)) + "\nSe Maskin → Maskinlogg och underhåll.";

    /// <summary>Maskin → Maskinlogg och underhåll… (or the button on a machine's row in Maskiner).</summary>
    private void MachineLog_Click(object sender, RoutedEventArgs e)
    {
        var machine = AppStorage.Current.Machines.Find(_document.Machine.Id) ?? AppStorage.Current.Machines.Active;
        ShowMachineLog(machine, this);
    }

    private void ShowMachineLog(MachineProfile machine, Window owner) =>
        new MachineLogWindow(machine, AppStorage.Current.Machines.MachineLogPath(machine.Id!)) { Owner = owner }.ShowDialog();
}
