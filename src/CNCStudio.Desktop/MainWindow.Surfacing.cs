using System.Globalization;
using System.Windows;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>Maskin → Planfräsa offerskivan…: the form, then the surfacing project replaces the open one.</summary>
public partial class MainWindow
{
    private async void Surfacing_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is not null) { Error("Ett program körs – vänta tills det är klart."); return; }
        // The connected machine's own $130/$131 when connected, otherwise the chosen machine's profile.
        var w = new SurfacingWindow(JogLimits, _tools) { Owner = this };
        if (w.ShowDialog() != true || w.Result is not { } doc) return;
        if (_document.Shapes.Count > 0 || _document.CamOperations.Count > 0)
        {
            var answer = AppDialog.Show(this, "Planfräsningen öppnas som ett nytt projekt i stället för det som är öppet.\n\nSpara det öppna projektet först?",
                "Planfräsa offerskivan", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                Save_Click(this, new RoutedEventArgs());
                if (_path is null) return;            // not saved (cancelled or refused): keep the open project
            }
        }
        _polyPoints.Clear(); _undo.Clear(); _redo.Clear();
        _document = doc; _camPlanner.Clear(); View3DPanel?.ResetView();
        _selected = null; _selection.Clear(); _selectionOrder.Clear(); _path = null;
        SyncMetadata(); HideStartScreen();
        SetWorkspace(Workspace.Machine);
        Render(); FitToMaterial();
        string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var (x, y) = WasteboardSurfacing.WorkZeroFromHome(_document);
        bool ready = Grbl is not null && _homedThisSession;

        // Z: the machine's own Z zero, when connected and homed (shown, and can be probed again).
        string zStep;
        if (ready && Grbl is GrblClient g)
        {
            try
            {
                var (top, _, _, _, error) = await ReadStockTopAsync(g);
                if (error is null)
                {
                    _document.Motion.StockTopMachineZ = Math.Round(top, 3);
                    RefreshMotionFields();
                    zStep = $"• Z: maskinens Z-nolla används (MPos Z {F(top)} vid offerskivans ovansida). Har du bytt fräs eller inte nollat Z på offerskivan – proba Z igen.\n";
                }
                else zStep = "• Z: proba Z (eller nolla Z med fräsen på offerskivan). " + error + "\n";
            }
            catch (Exception ex) { zStep = "• Z: proba Z (Z-nollan kunde inte läsas: " + ex.Message + ").\n"; }
        }
        else zStep = "• Z: sänk fräsen till offerskivans ovansida och nolla Z (eller proba Z).\n";

        StatusText.Text = $"Planfräsning {F(_document.MaterialWidth)} × {F(_document.MaterialHeight)} mm skapad – spara projektet om du vill köra den igen.";
        string text =
            $"Planfräsningen av {F(_document.MaterialWidth)} × {F(_document.MaterialHeight)} mm är ett nytt projekt (inte sparat ännu). Ytan syns i maskinvyn.\n\n" +
            "SÅ KÖR DU DEN:\n" +
            (ready ? "" : "• Anslut, sätt i planfräsen och homa maskinen ($H).\n") +
            $"• X/Y: X0 Y0 är ytans främre vänstra hörn, {F(x)} / {F(y)} mm från hemhörnet. Klicka Nolla X/Y vid materialet – maskinen rör sig inte.\n" +
            zStep +
            "• Klicka Ladda från projektet under KÖR PROGRAM (Maskinkontroll) och sedan Starta. Programmet kontrolleras som vanligt innan det körs.";
        if (!ready)
        {
            AppDialog.Show(this, text, "Planfräsa offerskivan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var zeroNow = AppDialog.Choose(this, text, "Planfräsa offerskivan",
            new[] { ("Nolla X/Y vid ytan nu", MessageBoxResult.Yes), ("Senare", MessageBoxResult.No) }, MessageBoxImage.Information, MessageBoxResult.Yes);
        if (zeroNow == MessageBoxResult.Yes && await ZeroAtPlacement(ask: false))
            StatusText.Text = $"X0 Y0 satt i ytans främre vänstra hörn ({F(x)} / {F(y)} mm från hemhörnet). Klicka Ladda från projektet.";
    }
}
