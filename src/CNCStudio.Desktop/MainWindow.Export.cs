using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CNCStudio.Core;
using CNCStudio.Core.GCode;
using Microsoft.Win32;

namespace CNCStudio.Desktop;

/// <summary>
/// G-code export: recompute every path, refuse if anything is unsafe or unchecked, show a summary
/// with all warnings for confirmation, then save. One program per tool (no tool changer), each verified
/// independently before saving.
/// </summary>
public partial class MainWindow
{
    private void ExportGCode_Click(object sender, RoutedEventArgs e)
    {
        if (GenerateCheckedJobs("Exportera G-code") is not { } jobs) return;
        if (jobs.Count == 1)
        {
            var result = jobs[0].Result;
            if (!ConfirmProgram(jobs[0], "Exportera G-code", "Exportera")) return;
            var dialog = new SaveFileDialog
            {
                Filter = "G-code för GRBL (*.nc)|*.nc|G-code (*.gcode)|*.gcode",
                FileName = SafeFileName(_document.Name) + ".nc", InitialDirectory = ExportFolder()
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                // Plain ASCII with CRLF; every line has already been checked to be printable ASCII.
                File.WriteAllText(dialog.FileName, result.Text, Encoding.ASCII);
                StatusText.Text = $"G-code sparad: {dialog.FileName} ({result.Lines.Count} rader, {result.Warnings.Count} varningar).";
            }
            catch (Exception ex) { Error("Kunde inte spara G-code: " + ex.Message); }
            return;
        }

        // Several tools and no tool changer: one numbered program per tool.
        if (!ConfirmJobs(jobs, "Exportera G-code", "Exportera")) return;
        var save = new SaveFileDialog
        {
            Title = $"Grundnamn för {jobs.Count} program (ett per verktyg)",
            Filter = "G-code för GRBL (*.nc)|*.nc|G-code (*.gcode)|*.gcode",
            FileName = SafeFileName(_document.Name) + ".nc", OverwritePrompt = false, InitialDirectory = ExportFolder()
        };
        if (save.ShowDialog(this) != true) return;
        var folder = Path.GetDirectoryName(save.FileName)!;
        var baseName = Path.GetFileNameWithoutExtension(save.FileName);
        var ext = Path.GetExtension(save.FileName);
        var files = jobs.Select(j => Path.Combine(folder, $"{baseName} {j.Number} av {j.Count} - {SafeFileName(j.ToolLabel)}{ext}")).ToList();
        var existing = files.Where(File.Exists).Select(Path.GetFileName).ToList();
        if (existing.Count > 0 && AppDialog.Show(this, "De här filerna finns redan och skrivs över:\n\n" + string.Join("\n", existing.Select(n => "• " + n)) + "\n\nSkriv över?",
                "Exportera G-code", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try
        {
            for (int i = 0; i < jobs.Count; i++) File.WriteAllText(files[i], jobs[i].Result.Text, Encoding.ASCII);
            StatusText.Text = $"{jobs.Count} program sparade i {folder}: " + string.Join(", ", files.Select(Path.GetFileName)) + ".";
        }
        catch (Exception ex) { Error("Kunde inte spara G-code: " + ex.Message); }
    }

    /// <summary>G-code goes next to the project file, or in the Projekt folder for an unsaved project.</summary>
    private string ExportFolder() =>
        _path is not null && Path.GetDirectoryName(_path) is string dir && Directory.Exists(dir) ? dir : ProjectsFolder();

    /// <summary>
    /// Recomputes every path and generates one program per tool. If anything blocks it, the reasons are
    /// shown with fixes and "go there" buttons, and null is returned.
    /// </summary>
    private IReadOnlyList<GCodeJob>? GenerateCheckedJobs(string title)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        IReadOnlyList<GCodeJob> jobs;
        try
        {
            _camReport = _camPlanner.Review(_document, computeAll: true);
            (jobs, _) = GCodeGenerator.GenerateByTool(_document, _camReport);
        }
        finally { Mouse.OverrideCursor = null; }
        Render();

        if (jobs.Count == 0 || jobs.Any(j => !j.Result.Success))
        {
            var issues = GCodeGenerator.Blockers(_document, _camReport).Select(ExportIssue).ToList();
            foreach (var j in jobs.Where(j => !j.Result.Success))
            {
                foreach (var reason in j.Result.BlockingReasons)
                    issues.Add(new DialogIssue($"Program {j.Number} ({j.ToolLabel}): {reason}",
                        "Rätta det som anges och exportera igen."));
                foreach (var v in j.Result.VerifierIssues.Take(15))
                    issues.Add(new DialogIssue("Kontroll av programmet: " + v,
                        "Det här är ett internt fel i CNC Studio – programmet släpps inte ut. Spara projektet och rapportera felet."));
            }
            if (issues.Count == 0) issues.Add(new DialogIssue("Inget program kunde skapas.", "Kontrollera CAM-operationerna."));
            AppDialog.ShowIssues(this, title,
                issues.Count == 1 ? "G-code kan inte skapas ännu – en sak behöver åtgärdas:"
                                  : $"G-code kan inte skapas ännu – {issues.Count} saker behöver åtgärdas:",
                issues, MessageBoxImage.Error,
                "Exporten stoppas med flit tills allt är kontrollerat, så att maskinen aldrig kör ett okontrollerat program.");
            StatusText.Text = "G-code skapades inte – åtgärda punkterna i dialogen och försök igen.";
            return null;
        }
        return jobs;
    }

    private void AppendChecklist(StringBuilder summary, string? toolStep)
    {
        summary.AppendLine("FÖRE KÖRNING:")
            .AppendLine("• Homa maskinen med $H.")
            .AppendLine(_document.WorkZero == WorkZero.Center
                ? "• Sätt X0 Y0 i materialets MITT och Z0 på materialets ovansida (G54)."
                : "• Sätt X0 Y0 i materialets FRÄMRE VÄNSTRA hörn och Z0 på materialets ovansida (G54).");
        if (toolStep is not null) summary.AppendLine(toolStep);
        summary.AppendLine("• Kontrollera att laserläget är av ($32=0).")
            .AppendLine("• Kontrollera att fastspänningarna sitter där de är ritade.")
            .AppendLine(_document.Machine.Spindle == SpindleControl.Manual
                ? "• Fräsen är MANUELL: när programmet pausar (M0) – starta fräsen, ställ varvtalet och tryck Fortsätt (~)."
                : "• Spindeln startas av programmet (M3) – håll händerna borta.");
    }

    private static void AppendWarnings(StringBuilder summary, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;
        summary.AppendLine().AppendLine($"VARNINGAR ({warnings.Count}) – skrivs även i filens början:");
        foreach (var w in warnings.Take(10)) summary.AppendLine("• " + w);
        if (warnings.Count > 10) summary.AppendLine($"• … och {warnings.Count - 10} till.");
    }

    /// <summary>Summary with the checklist for one program; returns true if the user confirms.</summary>
    private bool ConfirmProgram(GCodeJob job, string title, string verb)
    {
        var result = job.Result;
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var ops = job.Operations;
        var summary = new StringBuilder();
        if (job.Count > 1) summary.AppendLine($"PROGRAM {job.Number} AV {job.Count}: {job.ToolLabel}").AppendLine();
        summary.AppendLine($"{ops.Count} operation(er), {result.Lines.Count} rader G-code.")
            .AppendLine(job.Count > 1 ? "" : RunTimeText())
            .AppendLine($"Djupast: {F(ops.Max(o => o.Operation.TotalDepth))} mm. Högsta Z: {F(job.Motion.MaxZ)} mm över materialet.")
            .AppendLine($"Matning {string.Join("/", ops.Select(o => F(o.Operation.FeedRate)).Distinct())} mm/min, " +
                        $"varvtal {string.Join("/", ops.Select(o => F(o.Operation.SpindleRpm)).Distinct())} varv/min.")
            .AppendLine();
        AppendChecklist(summary, job.Count > 1
            ? $"• VERKTYG: sätt i {job.ToolLabel} och NOLLA Z på materialytan med just det verktyget."
            : null);
        AppendWarnings(summary, result.Warnings);
        summary.AppendLine().Append(result.Warnings.Count > 0 ? $"{verb} trots varningarna?" : $"{verb}?");
        return AppDialog.Show(this, summary.ToString(), title, MessageBoxButton.OKCancel,
            result.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    /// <summary>Summary of a multi-tool job before saving all its programs.</summary>
    private bool ConfirmJobs(IReadOnlyList<GCodeJob> jobs, string title, string verb)
    {
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var summary = new StringBuilder()
            .AppendLine($"Projektet använder {jobs.Count} verktyg. Maskinen har ingen verktygsväxlare, så det blir {jobs.Count} program – kör dem i den här ordningen:")
            .AppendLine();
        foreach (var j in jobs)
            summary.AppendLine($"{j.Number}. {j.ToolLabel} – {j.Operations.Count} operation(er), djupast {F(j.Operations.Max(o => o.Operation.TotalDepth))} mm, {j.Result.Lines.Count} rader");
        summary.AppendLine().AppendLine(RunTimeText()).AppendLine();
        AppendChecklist(summary, "• FÖRE VARJE PROGRAM: sätt i dess verktyg och NOLLA Z på materialytan. X0 Y0 ändras inte.");
        var warnings = jobs.SelectMany(j => j.Result.Warnings).Distinct().ToList();
        AppendWarnings(summary, warnings);
        summary.AppendLine().Append(warnings.Count > 0 ? $"{verb} trots varningarna?" : $"{verb}?");
        return AppDialog.Show(this, summary.ToString(), title, MessageBoxButton.OKCancel,
            warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    /// <summary>A blocking reason with a concrete fix and an action that opens the right place.</summary>
    private DialogIssue ExportIssue(ExportBlocker b)
    {
        void ShowShape()
        {
            SetWorkspace(Workspace.Cam);
            if (b.ShapeId is Guid id && _document.Shapes.FirstOrDefault(s => s.Id == id) is DesignShape shape)
            { SelectShape(shape, toggle: false); _selected = shape; Render(); }
        }
        return b.Kind switch
        {
            ExportBlockerKind.StockTopUnknown => new(b.Message,
                "Homa maskinen ($H), jogga ned fräsen tills den nuddar materialet och nolla Z. Klicka sedan " +
                "\"Läs från maskinen ($#)\". Utan maskin: skriv in MPos Z vid materialytan (ett negativt tal, t.ex. -45.2) " +
                "och klicka \"Spara Z-höjder\".",
                "Gå till fältet", () => FocusField(ZMaxBox, ZExpander, "Fyll i MPos Z vid materialytan och klicka Spara Z-höjder.")),
            ExportBlockerKind.HomingOff => new(b.Message,
                "Slå på \"Homa vid start ($H)\" i Maskininställningar. Utan homing vet maskinen inte var Z-brytaren är.",
                "Öppna maskininställningar", () => MachineSettings_Click(this, new RoutedEventArgs())),
            ExportBlockerKind.StalePath or ExportBlockerKind.MissingPath => new(b.Message,
                "Låt CNC Studio räkna om körbanorna. Det kan ta några sekunder för stora former.",
                "Beräkna körbanor nu", () => { SetWorkspace(Workspace.Cam); PreviewContour_Click(this, new RoutedEventArgs()); }),
            ExportBlockerKind.NoOperations => new(b.Message,
                "Markera ett objekt, välj bearbetning (kontur eller ficka) i CAM-panelen och klicka Spara CAM.",
                "Gå till CAM", () => SetWorkspace(Workspace.Cam)),
            ExportBlockerKind.NoMotion => new(b.Message,
                "Kontrollera Z-höjderna under Maskin och att alla körbanor är beräknade.",
                "Gå till Z-höjder", () => FocusField(TravelHeightBox, ZExpander, null)),
            _ => CriticalIssue(b, ShowShape)
        };
    }

    private DialogIssue CriticalIssue(ExportBlocker b, Action showShape) => b.FindingCode switch
    {
        "FixtureCollision" => new(b.Message,
            "Flytta fastspänningen (dra den på maskinbordet) eller flytta objektet på materialet, så att fräsen inte når den.",
            "Visa maskinbordet", () => SetWorkspace(Workspace.Machine)),
        "NoMaterial" => new(b.Message,
            "Ange materialets bredd, höjd och tjocklek under Projekt och material och klicka Lägg till material.",
            "Gå till material", () => { SetWorkspace(Workspace.Design); FocusField(MaterialWidthBox, null, "Ange måtten och klicka Lägg till material."); }),
        "PlacementOutsideMachine" => new(b.Message,
            "Flytta materialet (och fastspänningarna) så att de ligger inom maskinens arbetsområde, eller gör materialet mindre. Körbanorna beräknas då igen.",
            "Gå till materialplacering", () => FocusField(StockXBox, null, "Ändra materialets X/Y och klicka Spara materialplacering – eller dra materialet i maskinvyn.")),
        "OutsideMachine" => new(b.Message,
            "Flytta materialet på maskinbordet så att hela banan ligger inom arbetsområdet, eller flytta objektet på materialet.",
            "Gå till materialplacering", () => FocusField(StockXBox, null, "Ändra materialets X/Y och klicka Spara materialplacering.")),
        "ZLimitExceeded" or "RapidInMaterial" or "InvalidMotionSettings" => new(b.Message,
            "Sänk förflyttningshöjden eller fastspänningarnas höjd, eller kontrollera MPos Z vid materialytan.",
            "Gå till Z-höjder", () => FocusField(TravelHeightBox, ZExpander, null)),
        "StockTopOutsideTravel" or "ZBottomExceeded" => new(b.Message,
            "Kontrollera MPos Z vid materialytan, Z-rörelsen i Maskininställningar och skärdjupet i CAM-panelen.",
            "Gå till MPos Z", () => FocusField(ZMaxBox, ZExpander, null)),
        "MissingShape" => new(b.Message,
            "Ångra borttagningen (Ctrl+Z) eller ta bort operationen.", "Gå till CAM", () => SetWorkspace(Workspace.Cam)),
        _ => new(b.Message,
            "Markera objektet och ändra fräs eller CAM-inställningar (djup, matning, fräsdiameter) i CAM-panelen.",
            "Visa objektet", showShape)
    };

    /// <summary>
    /// Switches to the workspace that owns <paramref name="field"/>, opens its section, scrolls it
    /// into view, focuses and highlights it for a few seconds.
    /// </summary>
    private void FocusField(System.Windows.Controls.TextBox field, System.Windows.Controls.Expander? section, string? hint)
    {
        SetWorkspace(Workspace.Machine);
        if (section is not null) section.IsExpanded = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            field.BringIntoView();
            field.Focus(); Keyboard.Focus(field); field.SelectAll();
            // Background, not border: the focus style of the TextBox template overrides the border colour.
            var oldBackground = field.ReadLocalValue(System.Windows.Controls.Control.BackgroundProperty);
            field.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xE3, 0xA8));
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (oldBackground == DependencyProperty.UnsetValue) field.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                else field.Background = (System.Windows.Media.Brush)oldBackground;
            };
            timer.Start();
            if (hint is not null) StatusText.Text = hint;
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "program" : clean;
    }
}
