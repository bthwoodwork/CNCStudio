using System.IO;
using System.Windows;
using CNCStudio.Core;
using CNCStudio.Core.GCode;
using Microsoft.Win32;

namespace CNCStudio.Desktop;

/// <summary>
/// Program → Öppna G-code-fil…: a program from another CAM program is read, checked for
/// GRBL (ExternalGCode), its facts shown, and loaded like a project's program. Starta then checks X/Y/Z against
/// the machine with the zero that is set, as for every program; Kontrollkör ($C), pause, stop, resume and the
/// machine log work the same.
/// </summary>
public partial class MainWindow
{
    /// <summary>The G-code file the loaded program came from; null for a project's program.</summary>
    private string? _loadedExternalPath;
    /// <summary>The loaded program came from outside the project (a file or the G-code editor), not from its CAM.</summary>
    private bool _loadedExternal;

    private void OpenGCode_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is not null || _macroRunner is not null) { Error("Ett program eller makro körs – vänta tills det är klart."); return; }
        var dialog = new OpenFileDialog
        {
            Title = "Öppna G-code från ett annat program",
            Filter = "G-code (*.nc;*.gcode;*.ngc;*.tap;*.cnc;*.gc;*.txt)|*.nc;*.gcode;*.ngc;*.tap;*.cnc;*.gc;*.txt|Alla filer|*.*",
            InitialDirectory = ExportFolder()
        };
        if (dialog.ShowDialog(this) != true) return;
        LoadExternal(dialog.FileName);
    }

    /// <summary>Reads, checks and (after the user has seen the facts) loads a G-code file. True when loaded.</summary>
    private bool LoadExternal(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { Error("Filen kunde inte läsas: " + ex.Message); return false; }
        return LoadExternalText(text, Path.GetFileName(path), path);
    }

    private double RapidRate => new[] { _document.Machine.MaxRateX, _document.Machine.MaxRateY }.Where(v => v is > 0).Select(v => v!.Value).DefaultIfEmpty(3000).Min();

    /// <summary>
    /// Checks and (after the user has seen the facts) loads G-code text as the program to run – from a file or
    /// from the G-code editor. <paramref name="path"/> is the file it is saved in, or null for unsaved text.
    /// </summary>
    private bool LoadExternalText(string text, string name, string? path)
    {
        if (_runner is not null || _macroRunner is not null) { Error("Ett program eller makro körs – vänta tills det är klart."); return false; }
        var program = ExternalGCode.Analyze(text, name, RapidRate);
        if (!program.Ok)
        {
            AppDialog.Show(this, $"\"{program.Name}\" kan inte köras med GRBL:\n\n" +
                string.Join("\n", program.Problems.Take(12).Select(p => "• " + p)) +
                (program.Problems.Count > 12 ? $"\n• … och {program.Problems.Count - 12} till." : "") +
                "\n\nVälj en GRBL-postprocessor i CAM-programmet och spara filen igen.",
                "Öppna G-code", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        var info = program.Info!;
        string text2 = $"{program.Name}\n\n{ExternalGCode.Summary(info)}\n\n" +
            (program.Warnings.Count > 0 ? "OBS:\n" + string.Join("\n", program.Warnings.Select(w => "• " + w)) + "\n\n" : "") +
            "FÖRE KÖRNING:\n" +
            "• Homa maskinen och sätt X0 Y0 och Z0 där CAM-programmet räknar med dem (ofta materialets hörn eller mitt och ovansidan).\n" +
            "• Starta kontrollerar att hela banan ryms i maskinen med den nollpunkt som är satt – djupet och höjden också.\n" +
            "• Filen kontrolleras inte mot materialet, fastspänningarna eller fräsen – det gör CAM-programmet. Kör gärna Kontrollkör ($C) först.";
        if (AppDialog.Choose(this, text2, "Öppna G-code",
                new[] { ("Ladda programmet", MessageBoxResult.OK), ("Avbryt", MessageBoxResult.Cancel) },
                program.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information, MessageBoxResult.OK) != MessageBoxResult.OK) return false;

        _jobLines = program.Lines;
        _jobName = program.Name;
        _loadedJob = null; _loadedJobs = null; _loadedStockTop = null; _nextProgramNumber = null;
        _loadedExternalPath = path; _loadedExternal = true;
        _loadedEstimate = info.EstimatedMinutes;
        JobInfoText.Text = $"{program.Name}: {program.Lines.Count} rader från fil. {RunTimeEstimator.Format(info.EstimatedMinutes)} ungefär.{LearnedText(info.EstimatedMinutes)} " +
            $"Utbredning {info.MaxX - info.MinX:0.#} × {info.MaxY - info.MinY:0.#} mm, djupast Z {info.MinZ:0.##}." +
            (program.Warnings.Count > 0 ? $" {program.Warnings.Count} saker att kontrollera (visades när filen öppnades)." : "");
        JobProgressBar.Value = 0; JobProgressText.Text = "";
        UpdateJobButtons();
        if (_machineView) RenderMachine(refreshCam: false);
        _jobToolWork = null; Update3DJob();
        StatusText.Text = path is null ? $"G-code laddad från editorn: {name}" : $"G-code laddad: {path}";
        return true;
    }

    /// <summary>Arkiv / Program → G-code-editor…: opens the loaded file, or an empty editor.</summary>
    private void GCodeEditor_Click(object sender, RoutedEventArgs e)
    {
        var path = _loadedExternalPath is string p && File.Exists(p) ? p : null;
        var editor = new GCodeEditorWindow(path, (text, name, file) =>
        {
            if (!LoadExternalText(text, name, file)) return false;
            SetWorkspace(Workspace.Machine);
            return true;
        }, RapidRate) { Owner = this };
        editor.Show();
    }
}
