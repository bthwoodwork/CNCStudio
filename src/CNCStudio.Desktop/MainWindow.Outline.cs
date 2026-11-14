using System.Globalization;
using System.Windows;
using CNCStudio.Core.GCode;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Program → Rita konturen…: the loaded program's outline (rectangle or outer shape) followed high above the
/// material, so you can see it fits the stock and clears the clamps before the run. Checked against the machine
/// like Starta, run like a macro (Stopp in the Program panel stops it).
/// </summary>
public partial class MainWindow
{
    private async void OutlineJob_Click(object sender, RoutedEventArgs e)
    {
        if (_jobLines is null) { Error("Ladda ett program först (Ladda från projektet eller Öppna G-code-fil…)."); return; }
        if (_runner is not null || _macroRunner is not null) { Error("Ett program eller makro körs – vänta tills det är klart."); return; }
        if (RequireIdle("Rita konturen", dialog: true) is not GrblClient g) return;
        if (!_homedThisSession) { Error("Kör homing först – konturen lyfter Z i maskinkoordinater, och de är kända först efter homing."); return; }

        var moves = GCodePath.Parse(_jobLines);
        var choice = AppDialog.Choose(this,
            "Fräsen lyfts högt upp och åker runt det programmet fräser – i luften, utan att gå ned – och tillbaka dit den står.\n\n" +
            "• Yttre form: formen runt hela banan, som ett gummiband.\n• Rektangel: programmets yttre rektangel.\n\n" +
            "Se att den ryms på materialet och går fri från fastspänningarna. Hastighet: joggens mm/min.",
            "Rita konturen", new[] { ("Yttre form", MessageBoxResult.Yes), ("Rektangel", MessageBoxResult.No), ("Avbryt", MessageBoxResult.Cancel) },
            MessageBoxImage.Question, MessageBoxResult.Yes);
        if (choice == MessageBoxResult.Cancel || choice == MessageBoxResult.None) return;
        var points = JobOutline.Points(moves, choice == MessageBoxResult.Yes ? OutlineShape.Hull : OutlineShape.Rectangle);
        if (points.Count == 0) { Error("Programmet skär ingenting – det finns ingen kontur att rita."); return; }

        double feed = Parse(JogFeed, out var f) && f > 0 ? f : 2000;
        feed = GrblMachineCommands.ClampFeed(feed, _document.Machine, xy: true, z: false);
        var st = g.LastStatus;
        double[]? wpos = st?.WPos ?? (st?.MPos is { Length: >= 3 } m && _lastWco is { Length: >= 3 } w ? new[] { m[0] - w[0], m[1] - w[1], m[2] - w[2] } : null);
        var lines = JobOutline.Lines(points, -_document.Machine.LimitMarginMm, feed, wpos is { } p ? (p[0], p[1]) : null);
        // The same check as Starta: with the zero that is set, the outline must stay inside the machine.
        if (!await CheckXyZero(g, lines, "Rita konturen")) return;

        var runner = new JobRunner(g, lines, store: null);
        _macroRunner = runner;
        UpdateKeepAwake();
        UpdateJobButtons(); RunMacroButton.IsEnabled = false;
        StatusText.Text = $"Ritar konturen ({points.Count} hörn) i luften …";
        try
        {
            var result = await runner.RunAsync();
            StatusText.Text = result.Outcome == JobOutcome.Finished ? "Konturen är ritad – fräsen står där den stod." : "Konturen avbröts: " + result.Message;
            if (result.Outcome == JobOutcome.Failed) AppDialog.Show(this, "Konturen stoppades: " + result.Message, "Rita konturen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { StatusText.Text = "Konturen avbröts: " + ex.Message; }
        finally
        {
            _macroRunner = null;
            UpdateKeepAwake();
            RunMacroButton.IsEnabled = true;
            UpdateJobButtons();
        }
    }
}
