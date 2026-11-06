using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CNCStudio.Core;
using CNCStudio.Core.GCode;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Running a program on the machine: load (with all CAM/G-code checks), $C check, start, pause/resume,
/// stop, feed override, and resuming an interrupted job (power cut, crash, error).
/// Progress is written to %APPDATA%\CNCStudio\job after every "ok" (JobProgressStore).
/// </summary>
public partial class MainWindow
{
    private readonly JobProgressStore _jobStore = new(JobProgressStore.DefaultFolder);
    private IReadOnlyList<string>? _jobLines;
    private string _jobName = "";
    private JobRunner? _runner;

    private void InitJobUi()
    {
        UpdateJobButtons();
        RefreshRecoveryBanner();
    }

    // ---- Load and check ----

    private void LoadJob_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is not null) return;
        if (GenerateCheckedProgram("Ladda program") is not GCodeResult result) return;
        _jobLines = result.Lines;
        _jobName = _document.Name;
        JobInfoText.Text = $"{_jobName}: {result.Lines.Count} rader. {RunTimeText()}" +
            (result.Warnings.Count > 0 ? $" {result.Warnings.Count} varningar – visas när du startar." : "");
        _loadedWarnings = result;
        JobProgressBar.Value = 0; JobProgressText.Text = "";
        UpdateJobButtons();
    }

    private GCodeResult? _loadedWarnings;

    private async void CheckJob_Click(object sender, RoutedEventArgs e)
    {
        if (_jobLines is null || RequireIdle("Kontrollkörning") is not GrblClient g) return;
        CheckJobButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(i => JobProgressText.Text = $"Kontrollkör ($C): rad {i} av {_jobLines.Count} …");
            var errors = await JobRunner.CheckAsync(g, _jobLines, progress);
            JobProgressText.Text = errors.Count == 0 ? "Kontrollkörning ($C): inga fel." : $"Kontrollkörning ($C): {errors.Count} fel.";
            AppDialog.Show(this, errors.Count == 0
                    ? "GRBL läste hela programmet i kontrolläge ($C) utan fel. Maskinen rörde sig inte."
                    : "GRBL hittade fel i programmet:\n\n" + string.Join("\n", errors.Take(15).Select(x => "• " + x)),
                "Kontrollkörning", MessageBoxButton.OK, errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { Error("Kontrollkörningen avbröts: " + ex.Message); }
        finally { UpdateJobButtons(); }
    }

    // ---- Start, pause, stop ----

    private async void StartJob_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is not null || _jobLines is null) return;
        if (RequireIdle("Start") is not GrblClient g) return;
        if (!_homedThisSession)
        { Error("Kör homing först (⌂ Homing). Utan homing är maskinkoordinaterna och Z-brytarens läge okända."); return; }
        if (_jobStore.LoadJob() is { Progress.Unfinished: true } &&
            AppDialog.Show(this, "Det finns ett avbrutet jobb som inte är klart. Startar du ett nytt jobb glöms det.\n\nStarta ändå?",
                "Starta", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        if (_loadedWarnings is not null && !ConfirmProgram(_loadedWarnings, "Starta programmet", "Starta")) return;
        var g54 = await ReadG54(g);
        _jobStore.Begin(_jobName, _jobLines, 0, g54);
        await RunJob(g, 0, null);
    }

    private async Task RunJob(GrblClient g, int startIndex, IReadOnlyList<string>? preamble)
    {
        var runner = new JobRunner(g, _jobLines!, _jobStore);
        _runner = runner;
        runner.ProgressChanged += () => Dispatcher.BeginInvoke(() => ShowJobProgress(runner));
        UpdateJobButtons();
        RecoveryBanner.Visibility = Visibility.Collapsed;
        StatusText.Text = startIndex == 0 ? "Programmet körs." : $"Jobbet återupptas från rad {startIndex + 1}.";
        var result = await runner.RunAsync(startIndex, preamble);
        _runner = null;
        ShowJobProgress(runner);
        UpdateJobButtons();
        JobProgressText.Text = result.Message;
        StatusText.Text = result.Message;
        if (result.Outcome == JobOutcome.Failed)
        {
            RefreshRecoveryBanner();
            AppDialog.Show(this, result.Message + "\n\nJobbets läge är sparat. När felet är åtgärdat (och maskinen homad) kan du återuppta det med \"Återuppta…\".",
                "Körningen stoppades", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (result.Outcome == JobOutcome.Finished)
            AppDialog.Show(this, "Programmet är klart." + (_document.Machine.Spindle == SpindleControl.Manual ? " Stäng av fräsen." : ""),
                "Klart", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowJobProgress(JobRunner r)
    {
        if (r.TotalLines == 0) return;
        JobProgressBar.Value = (double)r.AckedLines / r.TotalLines;
        JobProgressText.Text = $"Rad {r.AckedLines} av {r.TotalLines} ({100.0 * r.AckedLines / r.TotalLines:0} %)" +
            (r.CurrentLine is { Length: > 0 } c ? $"\n{c.Trim()}" : "");
    }

    private void PauseJob_Click(object sender, RoutedEventArgs e)
    {
        if (Grbl is not GrblClient g) return;
        // Hold (feed hold or the program's M0 pause) → resume with ~; otherwise feed hold.
        if (g.LastStatus?.State == "Hold") g.SendRealtime(GrblProtocol.CycleStartResume);
        else g.SendRealtime(GrblProtocol.FeedHold);
    }

    private async void StopJob_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is JobRunner r) { StopJobButton.IsEnabled = false; await r.StopAsync(); }
        else if (Grbl is GrblClient g) g.SendRealtime(GrblProtocol.FeedHold);
    }

    private void FeedOverride_Click(object sender, RoutedEventArgs e)
    {
        if (Grbl is not GrblClient g) return;
        g.SendRealtime(byte.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture));
    }

    /// <summary>Called for every status report.</summary>
    private void UpdateJobStatus(GrblStatus s)
    {
        if (s.Overrides is { Length: > 0 } ov) FeedOvText.Text = $"{ov[0]:0} %";
        bool hold = s.State == "Hold";
        PauseJobButton.Content = hold ? "▶ Fortsätt" : "❚❚ Paus";
        if (hold && _runner is not null && s.SubState == "0")
            JobProgressText.Text = "PAUSAD. " + (_document.Machine.Spindle == SpindleControl.Manual
                ? "Om programmet väntar på fräsen (M0): starta fräsen, ställ varvtalet och klicka Fortsätt."
                : "Klicka Fortsätt för att köra vidare.");
    }

    private void UpdateJobButtons()
    {
        if (StartJobButton is null) return;
        bool running = _runner is not null;
        LoadJobButton.IsEnabled = !running;
        CheckJobButton.IsEnabled = !running && _jobLines is not null && _grbl is not null;
        StartJobButton.IsEnabled = !running && _jobLines is not null && _grbl is not null;
        PauseJobButton.IsEnabled = _grbl is not null;
        StopJobButton.IsEnabled = _grbl is not null;
    }

    // ---- Resume after an interruption ----

    private void RefreshRecoveryBanner()
    {
        if (RecoveryBanner is null) return;
        var job = _runner is null ? _jobStore.LoadJob() : null;
        if (job is not { Progress.Unfinished: true } j) { RecoveryBanner.Visibility = Visibility.Collapsed; return; }
        var p = j.Progress;
        RecoveryBanner.Visibility = Visibility.Visible;
        RecoveryText.Text = $"\"{p.Name}\" avbröts {p.Updated:yyyy-MM-dd HH:mm}: rad {p.AckedLines} av {p.TotalLines} " +
            $"({100.0 * p.AckedLines / Math.Max(1, p.TotalLines):0} %)." + (p.Message is { Length: > 0 } m ? " " + m : "") +
            "\nAnslut, kör homing och klicka Återuppta.";
    }

    private async void ResumeJob_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is not null) return;
        if (_jobStore.LoadJob() is not { } job) { Error("Det sparade jobbet kunde inte läsas (filen saknas eller är ändrad)."); RefreshRecoveryBanner(); return; }
        if (Grbl is not GrblClient g) { Error("Anslut till maskinen först."); return; }
        if (!_homedThisSession)
        {
            AppDialog.Show(this, "Kör homing först (⌂ Homing i maskinstyrningen).\n\nEfter ett strömavbrott vet maskinen inte var den står. " +
                "Homing ger tillbaka maskinkoordinaterna; nollpunkten (G54) finns kvar i maskinens minne.",
                "Återuppta jobb", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (RequireIdle("Återuppta") is null) return;
        var (progress, lines) = job;
        var g54 = await ReadG54(g);
        if (progress.G54 is { Length: >= 3 } saved && g54 is { Length: >= 3 } now &&
            Enumerable.Range(0, 3).Any(i => Math.Abs(saved[i] - now[i]) > 0.02))
        {
            static string V(double[] v) => string.Join("  ", v.Take(3).Select(d => d.ToString("0.000", CultureInfo.InvariantCulture)).ToArray());
            if (AppDialog.Show(this, $"Nollpunkten (G54) har ändrats sedan jobbet startade:\n\nVid start:  {V(saved)}\nNu:            {V(now)}\n\n" +
                    "Fortsätter du nu fräser maskinen på ett annat ställe än tidigare. Återuppta bara om du med flit har nollat om på exakt samma punkt.\n\nÅteruppta ändå?",
                    "Återuppta jobb", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        }
        var plan = ResumePlanner.Plan(lines, progress.AckedLines, _document.Machine, _document.Motion);
        var text = plan.Description + "\n\n" +
            (plan.FromBeginning ? "" : "Så här kör maskinen in:\n" + string.Join("\n", plan.Preamble.Select(l => "• " + l)) + "\n\n") +
            "KONTROLLERA FÖRST:\n• Materialet och fastspänningarna har inte flyttats.\n• Inget ligger i vägen för fräsen.\n" +
            (_document.Machine.Spindle == SpindleControl.Manual
                ? "• Programmet pausar (M0) innan fräsen går ned – starta då fräsen och klicka Fortsätt.\n"
                : "• Spindeln startas av programmet – håll händerna borta.\n") +
            "\nÅteruppta jobbet?";
        if (AppDialog.Show(this, text, "Återuppta jobb", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _jobLines = lines;
        _jobName = progress.Name;
        _loadedWarnings = null;
        JobInfoText.Text = $"{_jobName}: {lines.Length} rader (återupptas).";
        _jobStore.Begin(_jobName, lines, plan.StartIndex, g54 ?? progress.G54);
        await RunJob(g, plan.StartIndex, plan.Preamble);
    }

    private void ForgetJob_Click(object sender, RoutedEventArgs e)
    {
        if (AppDialog.Show(this, "Glömma det avbrutna jobbet? Det kan inte återupptas efteråt.", "Glöm jobbet",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _jobStore.Clear();
        RefreshRecoveryBanner();
    }

    private static async Task<double[]?> ReadG54(GrblClient g)
    {
        try { return (await g.ReadParametersAsync()).TryGetValue("G54", out var v) ? v : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Demo machine only: cuts the power mid-job to try resuming.</summary>
    private void SimulatePowerCut_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMachine is null || Grbl is null)
        { Error("Anslut till demomaskinen (DEMO i portlistan) först. Det här går bara på demomaskinen."); return; }
        _demoMachine.PowerCycle();
        StatusText.Text = "Demomaskin: strömavbrott simulerat – maskinen startade om och tappade sin position.";
    }
}
