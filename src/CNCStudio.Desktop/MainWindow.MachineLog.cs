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
                Message = result.Message, Computer = AppStorage.ComputerName, Resumed = resumed,
                ProjectPath = resumed || _loadedExternalPath is not null ? null : _path,
                ProgramNumber = resumed ? null : _loadedJob is { Count: > 1 } j ? j.Number : null,
                GCodeFile = resumed ? null : _loadedExternalPath
            });
            due = log.DueTasks();
        });
        RefreshJobHistory();
        return due;
    }

    /// <summary>"UNDERHÅLL: …" lines for the job-done dialog, or "".</summary>
    private static string DueText(IReadOnlyList<MaintenanceTask> due) =>
        due.Count == 0 ? "" : "\n\nUNDERHÅLL ATT GÖRA:\n" + string.Join("\n", due.Select(t => "• " + t.Name)) + "\nSe Maskin → Maskinlogg och underhåll.";

    // ---- Program panel: recently run on this machine ----

    /// <summary>Fills Senast körda with the chosen machine's latest runs.</summary>
    private void RefreshJobHistory()
    {
        if (JobHistoryList is null) return;
        JobHistoryList.Items.Clear();
        var id = _document.Machine.Id ?? AppStorage.Current.Machines.Active.Id;
        if (id is null) return;
        MachineLog log;
        try { log = MachineLog.Load(AppStorage.Current.Machines.MachineLogPath(id)); }
        catch (Exception) { return; }
        foreach (var j in log.Jobs.AsEnumerable().Reverse().Take(15))
        {
            var (text, color) = j.Outcome switch
            {
                LoggedJobOutcome.Finished => ("klart", System.Windows.Media.Brushes.LightGreen),
                LoggedJobOutcome.Stopped => ("stoppat", System.Windows.Media.Brushes.Khaki),
                _ => ("avbrutet", System.Windows.Media.Brushes.LightCoral)
            };
            JobHistoryList.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Tag = j, Padding = new Thickness(4, 2, 4, 2),
                ToolTip = (j.GCodeFile is { } gf ? "G-code-fil: " + gf : j.ProjectPath ?? "Projektet okänt (osparat eller återupptaget)") + (j.Message.Length > 0 ? "\n" + j.Message : ""),
                Content = new System.Windows.Controls.StackPanel { Children =
                {
                    new System.Windows.Controls.TextBlock { Text = j.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                    new System.Windows.Controls.TextBlock { Text = $"{j.Start:yyyy-MM-dd HH:mm} · {MachineLog.Format(j.Duration)} · {text}", Foreground = color, FontSize = 10.5 }
                } }
            });
        }
        if (JobHistoryList.Items.Count == 0)
            JobHistoryList.Items.Add(new System.Windows.Controls.ListBoxItem { Content = "Inget kört ännu på den här maskinen.", IsEnabled = false });
        JobHistory_Changed(this, null!);
    }

    private void JobHistory_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        OpenHistoryButton.IsEnabled = _runner is null && SelectedHistoryJob is { } j && (j.GCodeFile ?? j.ProjectPath) is { } p && System.IO.File.Exists(p);

    private LoggedJob? SelectedHistoryJob => (JobHistoryList?.SelectedItem as System.Windows.Controls.ListBoxItem)?.Tag as LoggedJob;

    /// <summary>Opens the project of a recent run and loads the same program again (generated and checked anew).</summary>
    private void OpenFromHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is not null || SelectedHistoryJob is not { } job) return;
        if (job.GCodeFile is { } file)
        {
            if (!System.IO.File.Exists(file)) { Error("G-code-filen finns inte kvar: " + file); return; }
            LoadExternal(file);
            return;
        }
        if (job.ProjectPath is not { } path || !System.IO.File.Exists(path))
        { Error("Projektet finns inte kvar (eller körningen var från ett osparat projekt)."); return; }
        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            if (_document.Shapes.Count > 0 || _document.CamOperations.Count > 0)
            {
                var answer = AppDialog.Show(this, $"Öppna \"{System.IO.Path.GetFileName(path)}\" i stället för det öppna projektet?\n\nSpara det öppna projektet först?",
                    "Öppna från historiken", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.Yes) { Save_Click(this, new RoutedEventArgs()); if (_path is null) return; }
            }
            if (!OpenProject(path)) return;
            SetWorkspace(Workspace.Machine);
        }
        if (LoadProgram(job.ProgramNumber, "Laddat från historiken – kontrollera nollpunkten innan du startar."))
            StatusText.Text = $"{job.Name} laddat igen från historiken.";
    }

    /// <summary>Maskin → Maskinlogg och underhåll… (or the button on a machine's row in Maskiner).</summary>
    private void MachineLog_Click(object sender, RoutedEventArgs e)
    {
        var machine = AppStorage.Current.Machines.Find(_document.Machine.Id) ?? AppStorage.Current.Machines.Active;
        ShowMachineLog(machine, this);
    }

    private void ShowMachineLog(MachineProfile machine, Window owner) =>
        new MachineLogWindow(machine, AppStorage.Current.Machines.MachineLogPath(machine.Id!)) { Owner = owner }.ShowDialog();
}
