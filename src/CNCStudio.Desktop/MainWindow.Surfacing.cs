using System.Globalization;
using System.Windows;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Maskin → Planfräsa offerskivan…: the form, then the surfacing project replaces the open one.</summary>
public partial class MainWindow
{
    private void Surfacing_Click(object sender, RoutedEventArgs e)
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
        var (x, y) = WasteboardSurfacing.WorkZeroFromHome(_document);
        string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        StatusText.Text = $"Planfräsning {F(_document.MaterialWidth)} × {F(_document.MaterialHeight)} mm skapad – spara projektet om du vill köra den igen.";
        AppDialog.Show(this,
            $"Planfräsningen av {F(_document.MaterialWidth)} × {F(_document.MaterialHeight)} mm är ett nytt projekt (inte sparat ännu). Ytan syns i maskinvyn.\n\n" +
            "SÅ KÖR DU DEN:\n" +
            "• Sätt i planfräsen och homa maskinen ($H).\n" +
            $"• Jogga till ytans främre vänstra hörn: fräsmarkören i maskinvyn ska visa X {F(x)}  Y {F(y)} (från hemhörnet). Nolla X och Y där.\n" +
            "• Sänk fräsen till offerskivans ovansida och nolla Z (eller proba Z).\n" +
            "• Ladda program och starta. Programmet kontrolleras som vanligt innan det körs.",
            "Planfräsa offerskivan", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
