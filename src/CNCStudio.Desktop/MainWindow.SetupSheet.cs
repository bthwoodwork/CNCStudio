using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// CAM → Riggblad…: the set-up sheet (<see cref="SetupSheet"/>) as an HTML page in the default browser, from where it
/// is printed or saved as PDF (Skriv ut → Microsoft Print to PDF). Every path is computed first, as for export.
/// </summary>
public partial class MainWindow
{
    private void SetupSheet_Click(object sender, RoutedEventArgs e)
    {
        if (_document.CamOperations.Count == 0) { Error("Det finns inga CAM-operationer att göra ett riggblad för."); return; }
        string html;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _camReport = _camPlanner.Review(_document, computeAll: true);
            html = SetupSheet.Html(_document, _camReport);
        }
        finally { Mouse.OverrideCursor = null; }
        Render();
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "CNC Studio");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"Riggblad - {SafeFileName(_document.Name)}.html");
            File.WriteAllText(file, html, new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            StatusText.Text = "Riggbladet är öppnat i webbläsaren – skriv ut det eller spara som PDF (Skriv ut → Microsoft Print to PDF).";
        }
        catch (Exception ex) { Error("Riggbladet kunde inte öppnas: " + ex.Message); }
    }
}
