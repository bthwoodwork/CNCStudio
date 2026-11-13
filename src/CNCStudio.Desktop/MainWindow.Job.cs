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

    private void LoadJob_Click(object sender, RoutedEventArgs e) => LoadProgram(null);

    /// <summary>
    /// Generates and checks all programs, then loads one: <paramref name="number"/> directly, or the user
    /// picks (the next one suggested) when the project has several tools. The Z values the checks used are
    /// remembered: if Z is zeroed again afterwards, the program is checked again (see <see cref="OnZeroChanged"/>).
    /// </summary>
    private bool LoadProgram(int? number, string? note = null)
    {
        if (_runner is not null) return false;
        if (GenerateCheckedJobs("Ladda program") is not { } jobs) return false;
        var job = jobs[0];
        if (number is int n && jobs.FirstOrDefault(j => j.Number == n) is GCodeJob wanted) job = wanted;
        else if (jobs.Count > 1)
        {
            // No tool changer: pick which tool's program to run (the next one after the last loaded).
            int next = _loadedJob is { } last && last.Count == jobs.Count && last.Number < jobs.Count ? last.Number + 1 : 1;
            var picker = new ProgramPickerWindow(jobs, next) { Owner = this };
            if (picker.ShowDialog() != true || picker.Selected is null) return false;
            job = picker.Selected;
        }
        var result = job.Result;
        _jobLines = result.Lines;
        _jobName = job.Count > 1 ? $"{_document.Name} – program {job.Number} av {job.Count} ({job.ToolLabel})" : _document.Name;
        JobInfoText.Text = (note is null ? "" : note + "\n") + $"{_jobName}: {result.Lines.Count} rader." +
            (job.Count > 1 ? $" Fräs: {job.ToolLabel}. Z-nollan ska vara satt med den." : $" {RunTimeText()}") +
            (result.Warnings.Count > 0 ? $" {result.Warnings.Count} varningar – visas när du startar." : "");
        _loadedJob = job;
        _loadedStockTop = _document.Motion.StockTopMachineZ;
        _nextProgramNumber = null;
        JobProgressBar.Value = 0; JobProgressText.Text = "";
        UpdateJobButtons();
        if (_machineView) RenderMachine(refreshCam: false);   // draw the loaded program
        _jobToolWork = null; Update3DJob();
        return true;
    }

    /// <summary>The "MPos Z at the stock top" the loaded program was checked against.</summary>
    private double? _loadedStockTop;
    /// <summary>After a program of a multi-tool job: the program to load once Z is zeroed with the next tool.</summary>
    private int? _nextProgramNumber;

    /// <summary>
    /// Z was zeroed again (new tool or new zero). A loaded program was checked against the old Z, so it is
    /// checked and loaded again; after a finished program this loads the next one.
    /// </summary>
    private void OnZeroChanged()
    {
        if (_runner is not null) return;
        if (_nextProgramNumber is int next)
        {
            if (LoadProgram(next, "Z nollad med nya fräsen – nästa program är laddat och kontrollerat mot den nya Z-nollan."))
                StatusText.Text = $"Program {next} laddat. Starta när fräsen sitter i och materialet är klart.";
            return;
        }
        if (_jobLines is null || _loadedJob is null) return;
        if (_loadedStockTop is double before && _document.Motion.StockTopMachineZ is double now && Math.Abs(before - now) < 0.0005) return;
        int number = _loadedJob.Number;
        _jobLines = null; UpdateJobButtons();
        JobInfoText.Text = "Z nollades om efter att programmet laddades – det kontrolleras igen …";
        if (!LoadProgram(number, "Z nollades om – programmet är kontrollerat igen mot den nya Z-nollan."))
            JobInfoText.Text = "Z nollades om. Programmet är inte laddat – klicka Ladda från projektet.";
    }
    private GCodeJob? _loadedJob;

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
        if (!await CheckXyZero(g, _jobLines, "Starta")) return;
        if (_loadedJob is not null && !ConfirmProgram(_loadedJob, "Starta programmet", "Starta")) return;
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
        {
            MarkJobPathDone();
            string stop = _document.Machine.Spindle == SpindleControl.Manual ? " Stäng av fräsen." : "";
            if (_loadedJob is { Count: > 1 } done && done.Number < done.Count)
            {
                // Multi-tool job: the next program needs the next tool and Z zeroed with it. Unload this one so
                // it cannot be started again by mistake with the new tool in.
                int next = done.Number + 1;
                var tree = CamTree.Build(_document, _camReport);
                string tool = tree.Programs.FirstOrDefault(p => p.Number == next)?.ToolLabel ?? "nästa fräs";
                _nextProgramNumber = next;
                _jobLines = null; UpdateJobButtons();
                if (_machineView) RenderMachine(refreshCam: false);
                Update3DJob();
                JobInfoText.Text = $"Program {done.Number} av {done.Count} klart. Nästa: program {next} – {tool}. Byt fräs och klicka Nolla Z – programmet laddas då automatiskt.";
                AppDialog.Show(this,
                    $"Program {done.Number} av {done.Count} är klart.{stop}\n\n" +
                    $"NÄSTA: program {next} – {tool}\n" +
                    $"1. Byt till {tool}.\n" +
                    "2. Jogga ned till materialytan och klicka Nolla Z (X och Y ändras inte).\n" +
                    "   Programmet laddas då automatiskt och kontrolleras mot den nya Z-nollan.\n" +
                    "3. Klicka Starta.",
                    "Program klart", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                if (_loadedJob is { Count: > 1 } last) JobInfoText.Text = $"Alla {last.Count} program är klara.";
                AppDialog.Show(this, (_loadedJob is { Count: > 1 } all ? $"Alla {all.Count} program är klara." : "Programmet är klart.") + stop,
                    "Klart", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
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
        // Say why Starta is grey instead of leaving the user to guess.
        StartJobButton.ToolTip = running ? "Ett program körs."
            : _grbl is null ? "Anslut till maskinen först."
            : _jobLines is null ? "Inget program laddat – klicka Ladda från projektet först. Programmet skapas och kontrolleras då mot Z-nollan."
            : "Starta det laddade programmet.";
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(StartJobButton, true);
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
        if (!await CheckXyZero(g, lines, "Återuppta jobb")) return;
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
        _loadedJob = null;
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

    /// <summary>
    /// Before a run: with the work offset the machine really has (G54 + G92 from $#), does every move stay
    /// inside GRBL's soft limits? A forgotten or wrong X/Y zero is stopped here, with what to do, instead of
    /// by ALARM:2 after the run has begun. If the offsets cannot be read the user decides.
    /// </summary>
    private async Task<bool> CheckXyZero(GrblClient g, IReadOnlyList<string> lines, string title)
    {
        try
        {
            var p = await g.ReadParametersAsync();
            if (!p.TryGetValue("G54", out var g54) || g54.Length < 3) throw new InvalidOperationException("G54 saknas i svaret på $#.");
            var g92 = p.TryGetValue("G92", out var v) && v.Length >= 3 ? v : new double[3];
            var offset = new[] { g54[0] + g92[0], g54[1] + g92[1], g54[2] + g92[2] };
            var settings = _grblSettings ?? await g.ReadSettingsAsync();
            string hint = _document.WorkZero == WorkZero.Center ? "materialets mitt" : "materialets främre vänstra hörn";
            if (JobEnvelope.Check(GCodePath.Parse(lines), offset, settings, hint) is string problem)
            {
                AppDialog.Show(this, problem, title, MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Programmet startades inte: X/Y-nollan skulle föra fräsen utanför maskinen.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            return AppDialog.Show(this, "X/Y-nollan kunde inte kontrolleras mot maskinens arbetsområde: " + ex.Message +
                "\n\nKontrollera själv att X0 Y0 är satt där programmet förutsätter. Fortsätta ändå?",
                title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;
        }
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
