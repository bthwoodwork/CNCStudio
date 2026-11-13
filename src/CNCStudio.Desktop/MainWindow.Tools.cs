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
    private MaterialLibrary _materials = LoadMaterials();
    private static readonly ToolDefinition CustomTool = new() { Name = "(fyll i värdena nedan)", Category = "Egen fräs" };

    private static ToolLibrary LoadTools()
    {
        try { return ToolLibrary.Load(AppStorage.Current.ToolLibraryPath); }
        catch (Exception) { return ToolLibrary.Defaults(); }
    }

    private static MaterialLibrary LoadMaterials()
    {
        try { return MaterialLibrary.Load(AppStorage.Current.MaterialLibraryPath); }
        catch (Exception) { return MaterialLibrary.Defaults(); }
    }

    private void RefreshToolPicker()
    {
        if (CamToolPicker is null) return;
        var keep = _loadingCam;
        _loadingCam = true;
        try
        {
            CamToolPicker.ItemsSource = null;
            CamToolPicker.ItemsSource = new[] { CustomTool }
                .Concat(_tools.Tools.OrderBy(t => _tools.CategoryOrder(t.CategoryOrDefault)).ThenBy(t => t.Diameter).ThenBy(t => t.Name)).ToList();
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

    /// <summary>
    /// Choosing a tool fills its diameter and its cutting data for the project's material – or its
    /// default values when the tool has no data for that material (they can still be changed).
    /// </summary>
    private void CamTool_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingCam || CamToolPicker.SelectedItem is not ToolDefinition t || t == CustomTool) return;
        var v = t.CuttingFor(_document.MaterialId);
        CamDiameter.Text = F(t.Diameter);
        CamFeed.Text = F(v.FeedRate);
        CamPlunge.Text = F(v.PlungeRate);
        CamPass.Text = F(v.DepthPerPass);
        CamRpm.Text = F(v.SpindleRpm);
        UpdateCamSummary();
        StatusText.Text = t.Kind == ToolKind.VBit
            ? "V-fräs vald: den kan inte användas för kontur eller ficka (V-carving kommer senare)."
            : v.Material is string m ? $"Skärdata för {t.Name} i {m} från verktygsbiblioteket."
            : _document.MaterialName is string pm ? $"{t.Name} har ingen skärdata för {pm} – standardvärdena används. Lägg till den i Verktygsbiblioteket."
            : $"{t.Name}: standardvärden (välj materialtyp under Projekt och material för materialets skärdata).";
    }

    private ToolDefinition? SelectedTool => CamToolPicker?.SelectedItem is ToolDefinition t && t != CustomTool ? t : null;

    private void ManageTools_Click(object sender, RoutedEventArgs e)
    {
        // The window saves every change at once (Spara fräsen, new/removed tools, categories) and stays open.
        new ToolLibraryWindow(_tools, _materials, AppStorage.Current.ToolLibraryPath, save: library =>
        {
            try
            {
                library.Save(AppStorage.Current.ToolLibraryPath);
                var current = SelectedTool?.Name;
                _tools = library.Clone();
                RefreshToolPicker();
                SelectTool(current);
                StatusText.Text = $"Verktygsbiblioteket sparat ({_tools.Tools.Count} fräsar): {AppStorage.Current.ToolLibraryPath}";
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }) { Owner = this }.ShowDialog();
    }

    // ---- Materials ----

    private bool _syncingMaterial;

    private void ManageMaterials_Click(object sender, RoutedEventArgs e)
    {
        var w = new MaterialLibraryWindow(_materials, _tools, AppStorage.Current.MaterialLibraryPath) { Owner = this };
        if (w.ShowDialog() != true || w.Result is null) return;
        try
        {
            w.Result.Save(AppStorage.Current.MaterialLibraryPath);
            _materials = w.Result;
            RefreshMaterialPicker();
            StatusText.Text = $"Materialbiblioteket sparat ({_materials.Materials.Count} material): {AppStorage.Current.MaterialLibraryPath}";
        }
        catch (Exception ex) { Error("Kunde inte spara materialbiblioteket: " + ex.Message); }
    }

    private static readonly MaterialDefinition NoMaterial = new() { Name = "(ej valt – fräsarnas standardvärden)", Category = "–" };

    /// <summary>Shows the library's materials and selects the project's material (also one missing from this library).</summary>
    private void RefreshMaterialPicker()
    {
        if (MaterialPicker is null) return;
        _syncingMaterial = true;
        try
        {
            var items = new List<MaterialDefinition> { NoMaterial };
            items.AddRange(_materials.Materials.OrderBy(m => m.Category).ThenBy(m => m.Name));
            if (_document.MaterialId is Guid id && _materials.Find(id) is null)
                items.Add(new MaterialDefinition { Id = id, Name = (_document.MaterialName ?? "okänt") + " (finns inte i biblioteket)", Category = "Projekt" });
            MaterialPicker.ItemsSource = items;
            MaterialPicker.SelectedItem = items.FirstOrDefault(m => _document.MaterialId is Guid g && m.Id == g) ?? NoMaterial;
        }
        finally { _syncingMaterial = false; }
    }

    /// <summary>
    /// A new material for the project. Operations made with library tools can take that tool's cutting
    /// data for the material – only after confirmation, since it changes feeds and depths.
    /// </summary>
    private void Material_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMaterial || MaterialPicker.SelectedItem is not MaterialDefinition m) return;
        Guid? id = m == NoMaterial ? null : m.Id;
        if (id == _document.MaterialId) return;
        RememberChange();
        _document.MaterialId = id;
        _document.MaterialName = id is null ? null : _materials.Find(id)?.Name ?? _document.MaterialName;
        var affected = _document.CamOperations
            .Select(op => (op, tool: _tools.Tools.FirstOrDefault(t => t.Name == op.ToolName && Math.Abs(t.Diameter - op.ToolDiameter) < 1e-9)))
            .Where(x => x.tool is not null).ToList();
        if (affected.Count > 0 && AppDialog.Show(this,
                $"Materialet är nu {(id is null ? "inte valt" : m.Name)}.\n\nUppdatera matning, nedmatning, djup per pass och varvtal för {affected.Count} operation(er) " +
                "med fräsarnas skärdata för materialet (eller standardvärden där skärdata saknas)?",
                "Byt material", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            foreach (var (op, tool) in affected) tool!.ApplyTo(op, id);
            _camPlanner.Clear();
            RefreshCamSelection();
        }
        Render();
        StatusText.Text = id is null ? "Ingen materialtyp vald – fräsarnas standardvärden används." : $"Materialtyp: {m.Name}.";
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
        double len = 22 / _zoom, head = 4 / _zoom, size = 8 / _zoom, w = 1.4 / _zoom;
        void Axis(double x2, double y2, double hx, double hy, Brush brush, string name, double lx, double ly)
        {
            canvas.Children.Add(new Line { X1 = x, Y1 = y, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = w, IsHitTestVisible = false });
            var arrow = new Polygon { Fill = brush, IsHitTestVisible = false };
            arrow.Points.Add(new Point(x2 + hx * head * 1.6, y2 + hy * head * 1.6));
            arrow.Points.Add(new Point(x2 - hy * head, y2 + hx * head));
            arrow.Points.Add(new Point(x2 + hy * head, y2 - hx * head));
            canvas.Children.Add(arrow);
            var label = new TextBlock { Text = name, FontSize = 9.5 / _zoom, FontWeight = FontWeights.SemiBold,
                Foreground = brush, IsHitTestVisible = false };
            Canvas.SetLeft(label, lx); Canvas.SetTop(label, ly);
            canvas.Children.Add(label);
        }
        Axis(x + len, y, 1, 0, CanvasTheme.AxisX, "X", x + len + 8 / _zoom, y - 7 / _zoom);
        Axis(x, y - len, 0, -1, CanvasTheme.AxisY, "Y", x - 3 / _zoom, y - len - 20 / _zoom);
        var ring = new Ellipse { Width = size, Height = size, Fill = CanvasTheme.GripFill, Stroke = CanvasTheme.ZeroRing,
            StrokeThickness = 1.4 / _zoom, IsHitTestVisible = false };
        Canvas.SetLeft(ring, x - size / 2); Canvas.SetTop(ring, y - size / 2);
        canvas.Children.Add(ring);
        double core = 3 / _zoom;
        var dot = new Ellipse { Width = core, Height = core, Fill = CanvasTheme.ZeroRing, IsHitTestVisible = false };
        Canvas.SetLeft(dot, x - core / 2); Canvas.SetTop(dot, y - core / 2);
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
            var (tx, ty) = ToTable(p.X, p.Y, 0, 0);
            CursorText.Text = $"Bord X {F1(tx)}  Y {F1(ty)}";
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
