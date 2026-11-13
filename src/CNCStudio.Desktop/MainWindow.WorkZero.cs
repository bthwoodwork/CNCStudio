using System.Globalization;
using System.Windows;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>Maskinkontroll → Nolla X/Y vid materialet: X0 Y0 from the material placement, set with G10 L20 P0 – nothing moves.</summary>
public partial class MainWindow
{
    private async void ZeroAtPlacement_Click(object sender, RoutedEventArgs e) => await ZeroAtPlacement(ask: true);

    /// <summary>Sets X0 Y0 from the placement. <paramref name="ask"/> = show the numbers and ask first (the user already agreed otherwise). True when set.</summary>
    private async Task<bool> ZeroAtPlacement(bool ask)
    {
        if (RequireIdle("Nolla X/Y vid materialet", dialog: true) is not GrblClient g) return false;
        if (!_homedThisSession) { Error("Kör homing först (⌂ Homing). Utan homing vet maskinen inte var materialet ligger."); return false; }
        if (!_document.HasMaterial) { Error("Projektet har inget material – det finns ingen placering att nolla vid."); return false; }
        try
        {
            var settings = _grblSettings ?? await g.ReadSettingsAsync();
            var fromHome = WorkZeroPlacement.PointFromHome(_document);
            var zero = WorkZeroPlacement.MachinePosition(fromHome.X, fromHome.Y, settings);
            var mpos = g.LastStatus?.MPos;
            if (WorkZeroPlacement.Check(zero, settings, mpos) is string problem) { Error(problem); return false; }
            string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
            string where = _document.WorkZero == WorkZero.Center ? "materialets mitt" : "materialets främre vänstra hörn";
            if (ask && AppDialog.Show(this,
                    $"X0 Y0 sätts i {where} enligt materialplaceringen:\n\n" +
                    $"• {F(fromHome.X)} / {F(fromHome.Y)} mm från hemhörnet (som fräsmarkören i maskinvyn visar)\n" +
                    $"• MPos X {F(zero.X)}  Y {F(zero.Y)}\n\n" +
                    "Maskinen rör sig inte – bara nollpunkten (G54) ändras. Z ändras inte.\n\n" +
                    "Det stämmer bara om materialet ligger där det är ritat i maskinvyn. Vid planfräsning av offerskivan är det alltid så.",
                    "Nolla X/Y vid materialet", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK) return false;
            if (!await SendAsync(g, WorkZeroPlacement.SetLine(mpos!, zero))) return false;
            // Read back what the machine now has (G54 + G92 is what the program runs against).
            var p = await g.ReadParametersAsync();
            var g54 = p.TryGetValue("G54", out var a) ? a : null;
            var g92 = p.TryGetValue("G92", out var b) && b.Length >= 2 ? b : new double[3];
            if (g54 is not { Length: >= 2 } || Math.Abs(g54[0] + g92[0] - zero.X) > 0.01 || Math.Abs(g54[1] + g92[1] - zero.Y) > 0.01)
            { Error("Nollpunkten blev inte som beräknat – kontrollera X/Y innan du kör. Läs av med $# i GRBL-konsolen."); return false; }
            JogStatusText.Text = $"X0 Y0 satt i {where} (MPos X {F(zero.X)}  Y {F(zero.Y)}).";
            StatusText.Text = JogStatusText.Text;
            OnZeroChanged();
            return true;
        }
        catch (Exception ex) { Error("Kunde inte nolla X/Y: " + ex.Message); return false; }
    }
}
