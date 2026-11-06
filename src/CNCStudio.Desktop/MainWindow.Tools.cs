using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Tool library, work zero, cursor coordinates and run-time display.</summary>
public partial class MainWindow
{
    private ToolLibrary _tools = LoadTools();
    private static readonly ToolDefinition CustomTool = new() { Name = "(egen – fyll i nedan)" };

    private static ToolLibrary LoadTools()
    {
        try { return ToolLibrary.Load(ToolLibrary.DefaultPath); }
        catch (Exception) { return ToolLibrary.Defaults(); }
    }

    private void RefreshToolPicker()
    {
        if (CamToolPicker is null) return;
        var keep = _loadingCam;
        _loadingCam = true;
        try
        {
            CamToolPicker.ItemsSource = null;
            CamToolPicker.ItemsSource = new[] { CustomTool }.Concat(_tools.Tools).ToList();
            CamToolPicker.SelectedIndex = 0;
        }
        finally { _loadingCam = keep; }
    }

    /// <summary>Selects the library tool with this name, or "(egen)" if it is not in the library.</summary>
    private void SelectTool(string? name)
    {
        if (CamToolPicker?.ItemsSource is not IEnumerable<ToolDefinition> items) return;
        CamToolPicker.SelectedItem = items.FirstOrDefault(t => t != CustomTool && t.Name == name) ?? CustomTool;
    }

    /// <summary>Choosing a tool fills its diameter and default cutting settings (they can still be changed).</summary>
    private void CamTool_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCam || CamToolPicker.SelectedItem is not ToolDefinition t || t == CustomTool) return;
        CamDiameter.Text = F(t.Diameter);
        CamFeed.Text = F(t.FeedRate);
        CamPlunge.Text = F(t.PlungeRate);
        CamPass.Text = F(t.DepthPerPass);
        CamRpm.Text = F(t.SpindleRpm);
        UpdateCamSummary();
        if (t.Kind == ToolKind.VBit)
            StatusText.Text = "V-fräs vald: den kan inte användas för kontur eller ficka (V-carving kommer senare).";
    }

    private ToolDefinition? SelectedTool => CamToolPicker?.SelectedItem is ToolDefinition t && t != CustomTool ? t : null;

    private void ManageTools_Click(object sender, RoutedEventArgs e)
    {
        var w = new ToolLibraryWindow(_tools) { Owner = this };
        if (w.ShowDialog() != true || w.Result is null) return;
        try
        {
            w.Result.Save(ToolLibrary.DefaultPath);
            _tools = w.Result;
            var current = SelectedTool?.Name;
            RefreshToolPicker();
            SelectTool(current);
            StatusText.Text = $"Verktygsbiblioteket sparat ({_tools.Tools.Count} fräsar): {ToolLibrary.DefaultPath}";
        }
        catch (Exception ex) { Error("Kunde inte spara verktygsbiblioteket: " + ex.Message); }
    }

    // ---- Work zero ----

    private void WorkZero_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (WorkZeroPicker is null || _syncingZero) return;
        var zero = WorkZeroPicker.SelectedIndex == 1 ? WorkZero.Center : WorkZero.FrontLeft;
        if (zero == _document.WorkZero) return;
        RememberChange();
        _document.WorkZero = zero;
        Render();
        StatusText.Text = zero == WorkZero.Center
            ? "Nollpunkt: materialets mitt. Sätt X0 Y0 i mitten av materialet innan körning."
            : "Nollpunkt: materialets främre vänstra hörn.";
    }

    private bool _syncingZero;

    private void SyncWorkZero()
    {
        if (WorkZeroPicker is null) return;
        _syncingZero = true;
        WorkZeroPicker.SelectedIndex = _document.WorkZero == WorkZero.Center ? 1 : 0;
        _syncingZero = false;
    }

    /// <summary>Red dot with axes at X0 Y0 on the material (like the work-zero marker in other CAM tools).</summary>
    private void DrawWorkZeroMarker(Canvas canvas, double offsetX, double offsetY)
    {
        double x = _document.WorkZero == WorkZero.Center ? _document.MaterialWidth / 2 : 0;
        double y = _document.WorkZero == WorkZero.Center ? _document.MaterialHeight / 2 : _document.MaterialHeight;
        x += offsetX; y += offsetY;
        double len = 18 / _zoom, size = 9 / _zoom;
        canvas.Children.Add(new Line { X1 = x, Y1 = y, X2 = x + len, Y2 = y, Stroke = Brushes.Red, StrokeThickness = 2 / _zoom, IsHitTestVisible = false });
        canvas.Children.Add(new Line { X1 = x, Y1 = y, X2 = x, Y2 = y - len, Stroke = Brushes.LimeGreen, StrokeThickness = 2 / _zoom, IsHitTestVisible = false });
        var dot = new Ellipse { Width = size, Height = size, Fill = Brushes.Red, Stroke = Brushes.White, StrokeThickness = 1 / _zoom, IsHitTestVisible = false,
            ToolTip = "Nollpunkt X0 Y0 (G54)" };
        Canvas.SetLeft(dot, x - size / 2); Canvas.SetTop(dot, y - size / 2);
        canvas.Children.Add(dot);
    }

    // ---- Cursor coordinates ----

    private void UpdateCursorText(MouseEventArgs e)
    {
        if (CursorText is null) return;
        string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        if (_machineView)
        {
            var p = e.GetPosition(MachineCanvas);
            CursorText.Text = $"Bord X {F1(p.X)}  Y {F1(p.Y)}";
        }
        else
        {
            var p = e.GetPosition(DesignCanvas);
            var (wx, wy) = _document.ToWork(p.X, p.Y);
            CursorText.Text = $"X {F1(wx)}  Y {F1(wy)}";
        }
    }

    // ---- Run time ----

    private string RunTimeText()
    {
        if (_camReport.Motion.Moves.Count == 0) return "";
        var t = RunTimeEstimator.Estimate(_document, _camReport);
        return $"Uppskattad körtid: {RunTimeEstimator.Format(t.TotalMinutes)} " +
               $"(fräsning {RunTimeEstimator.Format(t.CuttingMinutes)}, förflyttning {RunTimeEstimator.Format(t.RapidMinutes)})" +
               (t.ManualPauses > 0 ? $" + {t.ManualPauses} manuell(a) paus(er)" : "") +
               (t.RapidRateAssumed ? ". Snabbhastighet antagen – läs $110–$112 från maskinen för bättre uppskattning." : ".") +
               " Acceleration ingår inte; verklig tid blir ofta något längre.";
    }
}
