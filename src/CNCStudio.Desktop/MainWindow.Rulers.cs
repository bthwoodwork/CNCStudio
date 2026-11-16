using System.Windows;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// The material's and the table's rulers, each on or off on its own (Visa menu and the 3D panel, saved per computer):
/// the material's are Design's 2D rulers and the ruler along the material in 3D, the table's are Maskin's 2D rulers
/// and the ruler along the table in 3D.
/// </summary>
public partial class MainWindow
{
    private bool StockRulerOn => AppStorage.Current.Settings.ShowStockRuler != false;
    private bool TableRulerOn => AppStorage.Current.Settings.ShowTableRuler != false;

    /// <summary>At start: the saved choice into the menu, the 2D rulers and the 3D panel.</summary>
    private void InitRulers()
    {
        StockRulerMenuItem.IsChecked = StockRulerOn;
        TableRulerMenuItem.IsChecked = TableRulerOn;
        NotifySoundMenuItem.IsChecked = NotifySoundOn;
        View3DPanel.SetRulers(StockRulerOn, TableRulerOn);
        View3DPanel.RulersChanged += (stock, table) => SetRulers(stock, table);
        ApplyRulerVisibility();
    }

    private void RulerMenu_Click(object sender, RoutedEventArgs e) =>
        SetRulers(StockRulerMenuItem.IsChecked, TableRulerMenuItem.IsChecked);

    private void SetRulers(bool stock, bool table)
    {
        AppStorage.Current.Settings.ShowStockRuler = stock;
        AppStorage.Current.Settings.ShowTableRuler = table;
        try { AppStorage.Current.SaveSettings(); } catch (Exception) { /* only a view setting */ }
        StockRulerMenuItem.IsChecked = stock;
        TableRulerMenuItem.IsChecked = table;
        View3DPanel.SetRulers(stock, table);
        ApplyRulerVisibility();
    }

    /// <summary>The 2D rulers belong to the material in Design and to the table in Maskin.</summary>
    private void ApplyRulerVisibility()
    {
        if (TopRuler is null || LeftRuler is null || RulerCorner is null) return;
        var v = (_machineView ? TableRulerOn : StockRulerOn) ? Visibility.Visible : Visibility.Collapsed;
        TopRuler.Visibility = LeftRuler.Visibility = RulerCorner.Visibility = v;
        _rulerCache = null;
        if (v == Visibility.Visible) RefreshRulers();
    }
}
