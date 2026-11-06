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
/// with all warnings for confirmation, then save. The program is verified independently before saving.
/// </summary>
public partial class MainWindow
{
    private void ExportGCode_Click(object sender, RoutedEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        GCodeResult result;
        try
        {
            _camReport = _camPlanner.Review(_document, computeAll: true);
            result = GCodeGenerator.Generate(_document, _camReport);
        }
        finally { Mouse.OverrideCursor = null; }
        Render();

        if (!result.Success)
        {
            var text = "G-code kan inte skapas:\n\n" + string.Join("\n", result.BlockingReasons.Select(r => "• " + r));
            if (result.VerifierIssues.Count > 0)
                text += "\n\nKontroll av programmet:\n" + string.Join("\n", result.VerifierIssues.Take(15).Select(i => "• " + i));
            MessageBox.Show(this, text, "Exportera G-code", MessageBoxButton.OK, MessageBoxImage.Stop);
            StatusText.Text = "G-code skapades inte – se orsakerna i dialogen.";
            return;
        }

        var plan = _camReport.Motion;
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var ops = _camReport.Operations;
        var summary = new StringBuilder()
            .AppendLine($"{ops.Count} operation(er), {result.Lines.Count} rader G-code.")
            .AppendLine(RunTimeText())
            .AppendLine($"Djupast: {F(ops.Max(o => o.Operation.TotalDepth))} mm. Högsta Z: {F(plan.MaxZ)} mm över materialet.")
            .AppendLine($"Matning {string.Join("/", ops.Select(o => F(o.Operation.FeedRate)).Distinct())} mm/min, " +
                        $"varvtal {string.Join("/", ops.Select(o => F(o.Operation.SpindleRpm)).Distinct())} varv/min.")
            .AppendLine()
            .AppendLine("FÖRE KÖRNING:")
            .AppendLine("• Homa maskinen med $H.")
            .AppendLine(_document.WorkZero == WorkZero.Center
                ? "• Sätt X0 Y0 i materialets MITT och Z0 på materialets ovansida (G54)."
                : "• Sätt X0 Y0 i materialets FRÄMRE VÄNSTRA hörn och Z0 på materialets ovansida (G54).")
            .AppendLine("• Kontrollera att laserläget är av ($32=0).")
            .AppendLine("• Kontrollera att fastspänningarna sitter där de är ritade.")
            .AppendLine(_document.Machine.Spindle == SpindleControl.Manual
                ? "• Fräsen är MANUELL: när programmet pausar (M0) – starta fräsen, ställ varvtalet och tryck Fortsätt (~)."
                : "• Spindeln startas av programmet (M3) – håll händerna borta.");
        if (result.Warnings.Count > 0)
        {
            summary.AppendLine().AppendLine($"VARNINGAR ({result.Warnings.Count}) – skrivs även i filens början:");
            foreach (var w in result.Warnings.Take(10)) summary.AppendLine("• " + w);
            if (result.Warnings.Count > 10) summary.AppendLine($"• … och {result.Warnings.Count - 10} till.");
        }
        summary.AppendLine().Append(result.Warnings.Count > 0 ? "Exportera trots varningarna?" : "Exportera?");
        if (MessageBox.Show(this, summary.ToString(), "Exportera G-code", MessageBoxButton.OKCancel,
                result.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question,
                MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        var dialog = new SaveFileDialog
        {
            Filter = "G-code för GRBL (*.nc)|*.nc|G-code (*.gcode)|*.gcode",
            FileName = SafeFileName(_document.Name) + ".nc"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Plain ASCII with CRLF; every line has already been checked to be printable ASCII.
            File.WriteAllText(dialog.FileName, result.Text, Encoding.ASCII);
            StatusText.Text = $"G-code sparad: {dialog.FileName} ({result.Lines.Count} rader, {result.Warnings.Count} varningar).";
        }
        catch (Exception ex) { Error("Kunde inte spara G-code: " + ex.Message); }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "program" : clean;
    }
}
