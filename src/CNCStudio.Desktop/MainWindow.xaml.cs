using System.IO;
using System.Globalization;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;
using Microsoft.Win32;

namespace CNCStudio.Desktop;

public partial class MainWindow : Window
{
    private CncDocument _document = new();
    private bool _machineView;
    private bool _fixtureDrawing;
    private FixtureBlock? _selectedFixture;
    private Point? _machineDragStart;
    private bool _movingStock;
    private bool _movingFixture;
    private Point _machineDragOriginal;
    private readonly Brush _fixtureBrush = new SolidColorBrush(Color.FromArgb(195, 205, 101, 56));
    private DesignShape? _selected;
    private Point? _start;
    private Point _original;
    private Tool _tool = Tool.Select;
    private bool _moving;
    private bool _marqueeActive;
    private Point _marqueeStart, _marqueeEnd;
    private bool _marqueeAdd;
    private bool _drawing;
    private bool _resizing;
    private ResizeHandle? _activeHandle;
    private double _resizeWidth, _resizeHeight;
    private readonly HashSet<DesignShape> _selection = new();
    private readonly Dictionary<DesignShape, Point> _moveOrigins = new();
    private double _zoom = 1.0;
    private readonly Canvas _gridLayer = new() { IsHitTestVisible = false };
    // Stable grid/ruler visuals are rebuilt only when view coordinates change,
    // not each time an object is dragged or resized.
    private (double Zoom, double X, double Y, double W, double H, double MatW, double MatH)? _gridCache;
    private (double Zoom, double X, double Y, double W, double H, double MatW, double MatH)? _rulerCache;
    private bool _panning;
    private Point _panStart;
    private Point _panOrigin;
    private bool _propertiesVisible = true;
    private bool _camVisible;
    private readonly Dictionary<string, Window> _floatingPanels = new();
    private readonly Dictionary<string, double> _savedPanelWidths = new() { ["Tools"] = 144, ["Properties"] = 250, ["Machine"] = 300, ["Cam"] = 300 };
    private bool _closingMain;
    private bool _loadingCam;
    private bool _showContourPreview = true;
    private sealed record ResizeHandle(DesignShape Shape, string Corner);
    private string? _path;
    private readonly Dictionary<char, double> _position = new() { ['X'] = 125, ['Y'] = 85, ['Z'] = -2.5 };
    private enum Tool { Select, Rectangle, Ellipse, Polyline }
    private readonly List<Point> _polyPoints = new();
    private Point _polyHover;
    private bool _snapEnabled = true;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private bool _gestureSaved;
    private readonly List<Guid> _selectionOrder = new();
    private void RememberChange()
    {
        _undo.Push(JsonSerializer.Serialize(_document));
        if (_undo.Count > 60)
        {
            var recent = _undo.Take(60).Reverse().ToArray();
            _undo.Clear();
            foreach (var item in recent) _undo.Push(item);
        }
        _redo.Clear();
    }
    private void RestoreSnapshot(string snapshot)
    {
        _document = JsonSerializer.Deserialize<CncDocument>(snapshot) ?? new CncDocument();
        _selected = null;
        _selection.Clear();
        _selectionOrder.Clear();
        _start = null;
        _polyPoints.Clear();
        _moving = false;
        _marqueeActive = false;
        _drawing = false;
        _resizing = false;
        _selectedFixture = null;
        SyncMetadata();
        Render();
    }
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _redo.Push(JsonSerializer.Serialize(_document));
        RestoreSnapshot(_undo.Pop());
        StatusText.Text = "Ångrat senaste ändringen.";
    }
    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _undo.Push(JsonSerializer.Serialize(_document));
        RestoreSnapshot(_redo.Pop());
        StatusText.Text = "Gjort om senaste ändringen.";
    }
    private static Geometry ShapeGeometry(DesignShape s)
    {
        var rect = new Rect(s.X, s.Y, s.Width, s.Height);
        if (s.Kind == ShapeKind.Rectangle) return new RectangleGeometry(rect);
        if (s.Kind == ShapeKind.Ellipse) return new EllipseGeometry(rect);
        var geo = new PathGeometry { FillRule = FillRule.EvenOdd };
        foreach (var contour in s.Contours ?? new())
        {
            if (contour.Points.Count < 2) continue;
            var start = contour.Points[0];
            var fig = new PathFigure
            {
                StartPoint = new Point(s.X + start.X * s.Width, s.Y + start.Y * s.Height),
                IsClosed = contour.Closed,
                IsFilled = contour.Closed
            };
            for (int i = 1; i < contour.Points.Count; i++)
                fig.Segments.Add(new LineSegment(new Point(s.X + contour.Points[i].X * s.Width,
                    s.Y + contour.Points[i].Y * s.Height), true));
            geo.Figures.Add(fig);
        }
        geo.Freeze();
        return geo;
    }
    private static DesignShape ShapeFromGeometry(Geometry input)
    {
        // 0.15 mm maximum flattening deviation for curves. Stored contours remain editable,
        // not raster pixels. The imported vector path is suitable for later CAM validation.
        var flat = input.GetFlattenedPathGeometry(0.15, ToleranceType.Absolute);
        var bounds = flat.Bounds;
        if (bounds.IsEmpty || bounds.Width <= .00001 || bounds.Height <= .00001)
            throw new InvalidOperationException("Operationen gav ingen yta. Kontrollera att objekten överlappar.");
        var output = new DesignShape { Kind = ShapeKind.VectorPath, X = bounds.X, Y = bounds.Y,
            Width = bounds.Width, Height = bounds.Height };
        foreach (var fig in flat.Figures)
        {
            if (!fig.IsClosed || !fig.IsFilled) continue;
            var points = new List<Point> { fig.StartPoint };
            foreach (var segment in fig.Segments)
            {
                if (segment is PolyLineSegment many) points.AddRange(many.Points);
                else if (segment is LineSegment line) points.Add(line.Point);
            }
            if (points.Count > 1 && (points[0] - points[^1]).Length < .00001) points.RemoveAt(points.Count - 1);
            if (points.Count < 3) continue;
            output.Contours.Add(new VectorContour { Points = points.Select(pt =>
                new VectorPoint((pt.X - bounds.X) / bounds.Width, (pt.Y - bounds.Y) / bounds.Height)).ToList() });
        }
        if (output.Contours.Count == 0) throw new InvalidOperationException("Ingen sluten kontur kunde skapas.");
        return output;
    }
    private enum BooleanAction { Union, Difference, Intersection, Xor }
    private void BooleanApply(BooleanAction action)
    {
        var ordered = _selectionOrder.Select(id => _document.Shapes.FirstOrDefault(s => s.Id == id))
            .OfType<DesignShape>().Where(s => _selection.Contains(s)).Distinct().ToList();
        foreach (var missing in _document.Shapes.Where(s => _selection.Contains(s) && !ordered.Contains(s))) ordered.Add(missing);
        if (ordered.Count < 2) { Error("Markera minst två objekt med Ctrl+klick."); return; }
        if (ordered.Any(shape => shape.Kind == ShapeKind.VectorPath && shape.Contours.Any(c => !c.Closed)))
        { Error("Boolean kräver slutna konturer. Stäng polylinjen först."); return; }
        try
        {
            Geometry geometry = ShapeGeometry(ordered[0]);
            var mode = action switch
            {
                BooleanAction.Union => GeometryCombineMode.Union,
                BooleanAction.Difference => GeometryCombineMode.Exclude,
                BooleanAction.Intersection => GeometryCombineMode.Intersect,
                _ => GeometryCombineMode.Xor
            };
            for (int i = 1; i < ordered.Count; i++)
                geometry = Geometry.Combine(geometry, ShapeGeometry(ordered[i]), mode, null, 0.15, ToleranceType.Absolute);
            var result = ShapeFromGeometry(geometry);
            var linkedCam = _document.CamOperations.Count(op => ordered.Any(s => s.Id == op.ShapeId));
            RememberChange();
            _document.Shapes.RemoveAll(s => ordered.Contains(s));
            _document.CamOperations.RemoveAll(op => ordered.Any(s => s.Id == op.ShapeId));
            _document.Shapes.Add(result);
            _selection.Clear(); _selection.Add(result); _selected = result;
            Render();
            StatusText.Text = $"{action}: {ordered.Count} objekt blev en vektorform med {result.Contours.Count} kontur(er)." +
                (linkedCam > 0 ? " Tidigare CAM-kopplingar togs bort: konfigurera ny verktygsbana." : "");
        }
        catch (Exception ex) { Error("Boolean-operationen kunde inte slutföras: " + ex.Message); }
    }
    private void Union_Click(object sender, RoutedEventArgs e) => BooleanApply(BooleanAction.Union);
    private void Difference_Click(object sender, RoutedEventArgs e) => BooleanApply(BooleanAction.Difference);
    private void Intersection_Click(object sender, RoutedEventArgs e) => BooleanApply(BooleanAction.Intersection);
    private void Xor_Click(object sender, RoutedEventArgs e) => BooleanApply(BooleanAction.Xor);


    public MainWindow() { InitializeComponent(); RefreshInlaySettings(); Render(); }
    private void Window_Loaded(object sender, RoutedEventArgs e) { FitToMaterial(); InitGrblUi(); MarkWorkspace(); }
    private static double Clamp(double n, double lo, double hi) => Math.Max(lo, Math.Min(hi, n));
    private void UpdateZoomLabel()
    {
        ZoomLabel.Text = $"  |  Zoom {_zoom * 100:0}%";
        if (GridStepLabel is not null) GridStepLabel.Text = $"Rutnät: {GridStep(_zoom):0.##} mm";
    }
    private void ApplyZoom(double newZoom, Point anchor)
    {
        newZoom = Clamp(newZoom, .1, 8);
        if (Math.Abs(newZoom - _zoom) < .00001) return;
        var scale = newZoom / _zoom;
        DesignOffset.X = anchor.X - (anchor.X - DesignOffset.X) * scale;
        DesignOffset.Y = anchor.Y - (anchor.Y - DesignOffset.Y) * scale;
        MachineOffset.X = DesignOffset.X; MachineOffset.Y = DesignOffset.Y;
        _zoom = newZoom;
        DesignScale.ScaleX = DesignScale.ScaleY = newZoom;
        MachineScale.ScaleX = MachineScale.ScaleY = newZoom;
        UpdateZoomLabel();
        RefreshViewportDecorations();
    }
    private void FitToMaterial()
    {
        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        double areaWidth = _machineView ? _document.Machine.WorkWidthMm : _document.MaterialWidth;
        double areaHeight = _machineView ? _document.Machine.WorkLengthMm : _document.MaterialHeight;
        _zoom = Clamp(Math.Min((Viewport.ActualWidth - 60) / areaWidth,
            (Viewport.ActualHeight - 60) / areaHeight), .1, 8);
        DesignScale.ScaleX = DesignScale.ScaleY = _zoom;
        DesignOffset.X = (Viewport.ActualWidth - areaWidth * _zoom) / 2;
        DesignOffset.Y = (Viewport.ActualHeight - areaHeight * _zoom) / 2;
        MachineScale.ScaleX = MachineScale.ScaleY = _zoom;
        MachineOffset.X = DesignOffset.X; MachineOffset.Y = DesignOffset.Y;
        UpdateZoomLabel();
        RefreshViewportDecorations();
    }
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_panning && !_moving && _start is null) FitToMaterial();
    }
    private void Viewport_Wheel(object sender, MouseWheelEventArgs e)
    {
        ApplyZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), e.GetPosition(Viewport));
        e.Handled = true;
    }
    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = true;
        _panStart = e.GetPosition(Viewport);
        _panOrigin = new Point(DesignOffset.X, DesignOffset.Y);
        Viewport.CaptureMouse();
        e.Handled = true;
    }
    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(Viewport);
        DesignOffset.X = _panOrigin.X + p.X - _panStart.X;
        DesignOffset.Y = _panOrigin.Y + p.Y - _panStart.Y;
        MachineOffset.X = DesignOffset.X; MachineOffset.Y = DesignOffset.Y;
        RefreshViewportDecorations();
    }
    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = false;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
    }
    private void Fit_Click(object sender, RoutedEventArgs e) => FitToMaterial();
    private void ActualSize_Click(object sender, RoutedEventArgs e)
        => ApplyZoom(1, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    private void ToggleProperties_Click(object sender, RoutedEventArgs e) => SetPanelVisible("Properties", !_propertiesVisible);
    // Docking without a third-party dependency: each existing live panel is moved between
    // the main layout and an optional independent Window. Its controls retain their state.
    private (Border Panel, ColumnDefinition Column, ColumnDefinition? Splitter, int Index) GetPanel(string id) => id switch
    {
        "Tools" => (ToolsPanel, ToolsColumn, ToolsSplitterColumn, 0),
        "Properties" => (PropertiesPanel, PropertiesColumn, PropertiesSplitterColumn, 4),
        "Machine" => (MachinePanel, MachineColumn, MachineSplitterColumn, 6),
        "Cam" => (CamPanel, CamColumn, CamSplitterColumn, 8),
        _ => throw new ArgumentException("Okänd panel", nameof(id))
    };
    private void SetPanelVisible(string id, bool visible)
    {
        var (panel, column, splitter, index) = GetPanel(id);
        if (_floatingPanels.TryGetValue(id, out var floating))
        {
            if (visible) { floating.Show(); floating.Activate(); return; }
            // Closing the floating window docks it, then it can be hidden normally.
            floating.Close();
        }
        if (visible)
        {
            if (!LayoutGrid.Children.Contains(panel))
            {
                LayoutGrid.Children.Add(panel);
                Grid.SetColumn(panel, index);
            }
            panel.Visibility = Visibility.Visible;
            column.Width = new GridLength(_savedPanelWidths[id], GridUnitType.Pixel);
            if (splitter is not null) splitter.Width = new GridLength(5);
        }
        else
        {
            if (column.ActualWidth > 40) _savedPanelWidths[id] = column.ActualWidth;
            panel.Visibility = Visibility.Collapsed;
            column.Width = new GridLength(0);
            if (splitter is not null) splitter.Width = new GridLength(0);
        }
        _propertiesVisible = PropertiesPanel.Visibility == Visibility.Visible;
        _camVisible = CamPanel.Visibility == Visibility.Visible;
        if (id == "Cam" && visible) RefreshCamSelection();
        Dispatcher.BeginInvoke(new Action(FitToMaterial), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void ShowPanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string id)
            SetPanelVisible(id, true);
    }
    private void HidePanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string id)
            SetPanelVisible(id, false);
    }
    private void FloatPanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string id) return;
        if (_floatingPanels.TryGetValue(id, out var existing)) { existing.Activate(); return; }
        var (panel, column, splitter, _) = GetPanel(id);
        if (column.ActualWidth > 40) _savedPanelWidths[id] = column.ActualWidth;
        LayoutGrid.Children.Remove(panel);
        column.Width = new GridLength(0);
        if (splitter is not null) splitter.Width = new GridLength(0);
        var window = new Window
        {
            Title = "CNC Studio – " + (id == "Machine" ? "Maskinkontroll" : id == "Properties" ? "Egenskaper" : id == "Cam" ? "CAM" : "Ritverktyg"),
            Width = id == "Tools" ? 240 : id == "Machine" ? 380 : 340,
            Height = id == "Tools" ? 650 : 700,
            MinWidth = 190, MinHeight = 300,
            Owner = this, Background = Brushes.DarkSlateGray,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };
        _floatingPanels[id] = window;
        window.Closed += (_, _) =>
        {
            window.Content = null;
            _floatingPanels.Remove(id);
            LayoutGrid.Children.Add(panel);
            Grid.SetColumn(panel, GetPanel(id).Index);
            panel.Visibility = Visibility.Visible;
            column.Width = new GridLength(_savedPanelWidths[id]);
            if (splitter is not null) splitter.Width = new GridLength(5);
            _propertiesVisible = PropertiesPanel.Visibility == Visibility.Visible;
            _camVisible = CamPanel.Visibility == Visibility.Visible;
            if (!_closingMain) Dispatcher.BeginInvoke(new Action(FitToMaterial), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        window.Show();
        Dispatcher.BeginInvoke(new Action(FitToMaterial), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _closingMain = true;
        foreach (var window in _floatingPanels.Values.ToList()) window.Close();
        if (_grbl is not null) DisconnectGrbl("Programmet stängs.");
    }
    private enum Workspace { Design, Cam, Machine }
    private Workspace _workspace = Workspace.Design;

    private void DesignWorkspace_Click(object sender, RoutedEventArgs e) => SetWorkspace(Workspace.Design);
    private void CamWorkspace_Click(object sender, RoutedEventArgs e) => SetWorkspace(Workspace.Cam);
    private void MachineWorkspace_Click(object sender, RoutedEventArgs e) => SetWorkspace(Workspace.Machine);

    /// <summary>
    /// The one place that switches workspace: view (material or machine table), panels and the
    /// highlighted tab/menu item. Design and CAM show the material; Maskin shows the machine table.
    /// </summary>
    private void SetWorkspace(Workspace ws)
    {
        _workspace = ws;
        SetMachineView(ws == Workspace.Machine);
        switch (ws)
        {
            case Workspace.Design:
                SetPanelVisible("Tools", true); SetPanelVisible("Properties", true);
                SetPanelVisible("Cam", false); SetPanelVisible("Machine", true);
                StatusText.Text = "Arbetsyta Design – rita och redigera på materialet.";
                break;
            case Workspace.Cam:
                SetPanelVisible("Tools", true); SetPanelVisible("Properties", false);
                SetPanelVisible("Machine", false); SetPanelVisible("Cam", true);
                StatusText.Text = "Arbetsyta CAM – bearbetning och körbanor på materialet. Ingen G-code.";
                break;
            case Workspace.Machine:
                SetPanelVisible("Tools", false); SetPanelVisible("Cam", false);
                SetPanelVisible("Machine", true);
                SetPanelVisible("Properties", true); // fixture and Z settings live there
                StatusText.Text = "Arbetsyta Maskin – maskinbordet: materialplacering, fastspänningar och maskinstyrning.";
                break;
        }
        MarkWorkspace();
    }

    private void MarkWorkspace()
    {
        if (DesignTab is null) return;
        DesignTab.Tag = _workspace == Workspace.Design ? "Active" : null;
        CamTab.Tag = _workspace == Workspace.Cam ? "Active" : null;
        MachineTab.Tag = _workspace == Workspace.Machine ? "Active" : null;
        DesignMenuItem.IsChecked = _workspace == Workspace.Design;
        CamMenuItem.IsChecked = _workspace == Workspace.Cam;
        MachineMenuItem.IsChecked = _workspace == Workspace.Machine;
    }
    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var previous = _path;
        _path = null;
        Save_Click(sender, e);
        if (_path is null) _path = previous;
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void About_Click(object sender, RoutedEventArgs e)
        => MessageBox.Show(this, AppTitle + "\nMaskinvy och materialplacering, fastspänningar, CAM XY-förhandsvisning och automatisk CAM-kontroll.\nIngen GRBL-styrning, kameraanslutning eller körbar G-code finns ännu.", "Om CNC Studio", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Workspace shortcuts work everywhere, also while typing in a field.
        if (e.Key == Key.F1) { SetWorkspace(Workspace.Design); e.Handled = true; return; }
        if (e.Key == Key.F2) { SetWorkspace(Workspace.Cam); e.Handled = true; return; }
        if (e.Key == Key.F3) { SetWorkspace(Workspace.Machine); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (_fixtureDrawing && e.Key == Key.Escape)
        { SetFixtureDrawing(false); _machineDragStart = null;
          if (MachineCanvas.IsMouseCaptured) MachineCanvas.ReleaseMouseCapture(); RenderMachine();
          StatusText.Text = "Fastspänningsritning avbruten."; e.Handled = true; return; }
        if (_polyPoints.Count > 0 && e.Key == Key.Enter)
        { FinishPolyline(true); e.Handled = true; return; }
        if (_polyPoints.Count > 0 && e.Key == Key.Escape)
        { _polyPoints.Clear(); Render(); StatusText.Text = "Polylinje avbruten."; e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.S)
        { SaveAs_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        { Save_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.I)
        { ImportVector_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        { Open_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        { New_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
        { SelectAll_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.G)
        { Group_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.G)
        { Ungroup_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z)
        { Undo_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Y)
        { Redo_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Delete) { Delete_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.D && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { Duplicate_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F) { FitToMaterial(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        { _polyPoints.Clear(); _selected = null; _selection.Clear(); _selectionOrder.Clear(); _tool = Tool.Select; _start = null; _moving = false; _marqueeActive = false;
          _resizing = false; _activeHandle = null; if (DesignCanvas.IsMouseCaptured) DesignCanvas.ReleaseMouseCapture();
          Render(); e.Handled = true; }
    }
    private static double Snap(double n) => Math.Round(n, 0);
    private Point SnapPoint(Point point)
    {
        if (!_snapEnabled) return point;
        var threshold = 9 / _zoom;
        Point? closest = null;
        var best = threshold * threshold;
        void Candidate(double x, double y)
        {
            var d = (point.X - x) * (point.X - x) + (point.Y - y) * (point.Y - y);
            if (d < best) { best = d; closest = new Point(x, y); }
        }
        foreach (var shape in _document.Shapes)
        {
            if (shape.Kind == ShapeKind.VectorPath)
            {
                foreach (var contour in shape.Contours)
                    foreach (var p in contour.Points)
                        Candidate(shape.X + p.X * shape.Width, shape.Y + p.Y * shape.Height);
            }
            else
            {
                Candidate(shape.X, shape.Y);
                Candidate(shape.X + shape.Width, shape.Y);
                Candidate(shape.X, shape.Y + shape.Height);
                Candidate(shape.X + shape.Width, shape.Y + shape.Height);
            }
        }
        if (_polyPoints.Count > 0) Candidate(_polyPoints[0].X, _polyPoints[0].Y);
        return closest ?? new Point(Snap(point.X), Snap(point.Y));
    }
    private void PolylineTool_Click(object sender, RoutedEventArgs e)
    { _polyPoints.Clear(); _tool = Tool.Polyline; Render(); StatusText.Text = "Polylinje: klicka punkter, Enter = sluten, högerklick = öppen, Esc = avbryt. Shift+klick på objekt = ny punkt."; }
    private void ClosePolyline_Click(object sender, RoutedEventArgs e) => FinishPolyline(true);
    private void ToggleSnap_Click(object sender, RoutedEventArgs e)
    { _snapEnabled = !_snapEnabled; SnapButton.Content = _snapEnabled ? "Snappning: På" : "Snappning: Av"; StatusText.Text = _snapEnabled ? "Snappning till hörn, ändpunkter och 1 mm-rutnät." : "Snappning avstängd."; }
    private void FinishPolyline(bool closed)
    {
        if (_polyPoints.Count < (closed ? 3 : 2))
        { StatusText.Text = closed ? "Sluten polylinje kräver minst tre punkter." : "Öppen polylinje kräver minst två punkter."; return; }
        var pts = _polyPoints.ToList();
        if (pts.Count > 1 && (pts[0] - pts[^1]).Length < 0.00001) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < (closed ? 3 : 2)) { StatusText.Text = "För få olika punkter."; return; }
        var minX = pts.Min(pt => pt.X); var minY = pts.Min(pt => pt.Y);
        var maxX = pts.Max(pt => pt.X); var maxY = pts.Max(pt => pt.Y);
        var width = Math.Max(maxX - minX, 0.001); var height = Math.Max(maxY - minY, 0.001);
        var shape = new DesignShape { Kind = ShapeKind.VectorPath, X = minX, Y = minY,
            Width = width, Height = height,
            Contours = new List<VectorContour> { new() { Closed = closed,
                Points = pts.Select(pt => new VectorPoint((pt.X - minX)/width, (pt.Y-minY)/height)).ToList() } } };
        RememberChange();
        _document.Shapes.Add(shape);
        _polyPoints.Clear(); _selection.Clear(); _selectionOrder.Clear();
        _selection.Add(shape); _selectionOrder.Add(shape.Id); _selected = shape;
        _tool = Tool.Select; Render();
        StatusText.Text = closed ? "Sluten polylinje skapad." : "Öppen polylinje skapad (inte fräsbar som yta).";
    }
    private void Canvas_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool != Tool.Polyline || _polyPoints.Count == 0) return;
        FinishPolyline(false); e.Handled = true;
    }
    private static bool SegmentsIntersect(Point a, Point b, Point c, Point d)
    {
        static double Cross(Point p, Point q, Point r)
            => (q.X-p.X)*(r.Y-p.Y) - (q.Y-p.Y)*(r.X-p.X);
        var ab1 = Cross(a,b,c); var ab2 = Cross(a,b,d);
        var cd1 = Cross(c,d,a); var cd2 = Cross(c,d,b);
        return ab1 * ab2 < -1e-10 && cd1 * cd2 < -1e-10;
    }
    private static int CountSelfIntersections(DesignShape shape, VectorContour contour)
    {
        var pts = contour.Points.Select(v => new Point(shape.X + v.X * shape.Width, shape.Y + v.Y * shape.Height)).ToList();
        var n = contour.Closed ? pts.Count : pts.Count - 1;
        int count = 0;
        for (int i = 0; i < n; i++)
        for (int j = i + 2; j < n; j++)
        {
            if (contour.Closed && i == 0 && j == n-1) continue;
            if (SegmentsIntersect(pts[i], pts[(i+1)%pts.Count], pts[j], pts[(j+1)%pts.Count])) count++;
        }
        return count;
    }
    private void VectorCheck_Click(object sender, RoutedEventArgs e)
    {
        var open = _document.Shapes.SelectMany(sh => sh.Kind == ShapeKind.VectorPath
            ? sh.Contours.Where(c => !c.Closed).Select(c => (sh, c))
            : Enumerable.Empty<(DesignShape sh, VectorContour c)>()).ToList();
        var invalid = _document.Shapes.Where(sh => sh.Width <= 0 || sh.Height <= 0 ||
            (sh.Kind == ShapeKind.VectorPath && sh.Contours.Any(c => c.Points.Count < (c.Closed ? 3 : 2)))).ToList();
        var duplicates = 0; var intersections = 0;
        foreach (var sh in _document.Shapes.Where(s => s.Kind == ShapeKind.VectorPath))
            foreach (var c in sh.Contours) intersections += CountSelfIntersections(sh, c);
        for (int i = 0; i < _document.Shapes.Count; i++)
            for (int j = i + 1; j < _document.Shapes.Count; j++)
            {
                var a = _document.Shapes[i]; var b = _document.Shapes[j];
                if (a.Kind == b.Kind && Math.Abs(a.X-b.X)<.001 && Math.Abs(a.Y-b.Y)<.001 &&
                    Math.Abs(a.Width-b.Width)<.001 && Math.Abs(a.Height-b.Height)<.001 &&
                    (a.Kind != ShapeKind.VectorPath || JsonSerializer.Serialize(a.Contours)==JsonSerializer.Serialize(b.Contours)))
                    duplicates++;
            }
        var nearGap = open.Where(x => x.c.Points.Count >= 3 &&
            (new Point(x.sh.X + x.c.Points[0].X*x.sh.Width, x.sh.Y + x.c.Points[0].Y*x.sh.Height) -
             new Point(x.sh.X + x.c.Points[^1].X*x.sh.Width, x.sh.Y + x.c.Points[^1].Y*x.sh.Height)).Length <= .5).ToList();
        var report = $"Objekt: {_document.Shapes.Count}\nÖppna konturer: {open.Count}\nGlapp under 0,5 mm (möjligt att sluta): {nearGap.Count}\nOgiltiga objekt: {invalid.Count}\nMöjliga dubletter: {duplicates}\nSjälvskärningar (linjesegment): {intersections}\n\n" +
            "Kontrollen gör inte en fullständig CAD-topologianalys.";
        if (nearGap.Count > 0)
        {
            var response = MessageBox.Show(this, report + "\n\nVill du sluta de små glappen? Ändringen kan ångras med Ctrl+Z.",
                "Vektorkontroll", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (response == MessageBoxResult.Yes)
            {
                RememberChange();
                foreach (var (_, contour) in nearGap)
                {
                    contour.Closed = true;
                    // Remove near-duplicate last vertex to avoid a tiny closing edge.
                    if (contour.Points.Count > 3) contour.Points.RemoveAt(contour.Points.Count-1);
                }
                Render(); StatusText.Text = $"Slöt {nearGap.Count} konturer (ångra: Ctrl+Z).";
            }
        }
        else MessageBox.Show(this, report, "Vektorkontroll", MessageBoxButton.OK,
            invalid.Count > 0 || intersections > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
    private static string F(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
    private static bool Parse(TextBox box, out double result) => double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    private void Error(string message) => MessageBox.Show(this, message, "CNC Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
    // Visually synchronize the tool palette with the actual interaction mode.
    // This is called after automatic return to Select as well as menu/tool clicks.
    private void UpdateToolButtonStates()
    {
        SetToolButtonState(SelectToolButton, _tool == Tool.Select);
        SetToolButtonState(RectangleToolButton, _tool == Tool.Rectangle);
        SetToolButtonState(EllipseToolButton, _tool == Tool.Ellipse);
        SetToolButtonState(PolylineToolButton, _tool == Tool.Polyline);
    }

    private static void SetToolButtonState(Button button, bool active)
    {
        button.Background = new SolidColorBrush(active
            ? Color.FromRgb(20, 106, 169)
            : Color.FromRgb(49, 66, 85));
        button.Foreground = Brushes.White;
        button.BorderBrush = new SolidColorBrush(active
            ? Color.FromRgb(153, 215, 255)
            : Color.FromRgb(72, 92, 112));
        button.BorderThickness = active ? new Thickness(2) : new Thickness(1);
        button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        button.ToolTip = active ? "Aktivt verktyg" : "Välj verktyg";
    }

    // Grid stroke widths are expressed in physical screen pixels, not model millimetres.
    // Only the visible part of the stock is drawn to keep high-zoom rendering responsive.
    private void RefreshViewportDecorations()
    {
        if (_machineView) RenderMachine();
        else { RefreshAdaptiveGrid(); RefreshRulers(); }
    }

    private static double GridStep(double zoom) => zoom >= 1.5 ? 1 :
        zoom >= 0.75 ? 5 : zoom >= 0.3 ? 10 : 50;

    private static bool AtMultiple(double coordinate, double step)
        => Math.Abs(coordinate / step - Math.Round(coordinate / step)) < 0.00001;

    private void RefreshAdaptiveGrid()
    {
        var key = (_zoom, DesignOffset.X, DesignOffset.Y, Viewport.ActualWidth,
            Viewport.ActualHeight, _document.MaterialWidth, _document.MaterialHeight);
        if (_gridCache.HasValue && _gridCache.Value == key) return;
        _gridCache = key;
        _gridLayer.Children.Clear();
        _gridLayer.Width = _document.MaterialWidth;
        _gridLayer.Height = _document.MaterialHeight;
        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0 || _zoom <= 0) return;

        double step = GridStep(_zoom);
        double minX = Math.Max(0, -DesignOffset.X / _zoom);
        double maxX = Math.Min(_document.MaterialWidth, (Viewport.ActualWidth - DesignOffset.X) / _zoom);
        double minY = Math.Max(0, -DesignOffset.Y / _zoom);
        double maxY = Math.Min(_document.MaterialHeight, (Viewport.ActualHeight - DesignOffset.Y) / _zoom);
        if (maxX < minX || maxY < minY) return;

        // Constant 0.45-1.15px grid strokes regardless of zoom. Fine 1mm lines
        // are deliberately faint; every 10/50mm receives stronger emphasis.
        void AddGridLine(double v, bool vertical)
        {
            bool major50 = AtMultiple(v, 50);
            bool major10 = AtMultiple(v, 10);
            byte alpha = major50 ? (byte)145 : major10 ? (byte)105 : (byte)53;
            double stroke = (major50 ? 1.0 : major10 ? 0.75 : 0.48) / _zoom;
            var line = vertical
                ? new Line { X1 = v, X2 = v, Y1 = 0, Y2 = _document.MaterialHeight }
                : new Line { X1 = 0, X2 = _document.MaterialWidth, Y1 = v, Y2 = v };
            line.Stroke = new SolidColorBrush(Color.FromArgb(alpha, 126, 108, 84));
            line.StrokeThickness = stroke;
            line.IsHitTestVisible = false;
            _gridLayer.Children.Add(line);
        }
        for (double x = Math.Ceiling(minX / step) * step; x <= maxX + .00001; x += step)
            AddGridLine(x, true);
        for (double y = Math.Ceiling(minY / step) * step; y <= maxY + .00001; y += step)
            AddGridLine(y, false);
    }

    private static double ChooseRulerLabelStep(double zoom)
    {
        // Labels should remain readable at any zoom level (at least ~55 pixels apart).
        foreach (var step in new double[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000 })
            if (step * zoom >= 55) return step;
        return 10000;
    }

    private void RefreshRulers()
    {
        if (TopRuler is null || LeftRuler is null) return;
        var key = (_zoom, DesignOffset.X, DesignOffset.Y, Viewport.ActualWidth,
            Viewport.ActualHeight, _document.MaterialWidth, _document.MaterialHeight);
        if (_rulerCache.HasValue && _rulerCache.Value == key) return;
        _rulerCache = key;
        TopRuler.Children.Clear();
        LeftRuler.Children.Clear();
        if (Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0 || _zoom <= 0) return;

        double labelStep = ChooseRulerLabelStep(_zoom);
        double tickStep = GridStep(_zoom);
        double left = Math.Max(0, -DesignOffset.X / _zoom);
        double right = Math.Min(_document.MaterialWidth, (Viewport.ActualWidth - DesignOffset.X) / _zoom);
        double top = Math.Max(0, -DesignOffset.Y / _zoom);
        double bottom = Math.Min(_document.MaterialHeight, (Viewport.ActualHeight - DesignOffset.Y) / _zoom);
        if (left > right || top > bottom) return;
        var tickBrush = new SolidColorBrush(Color.FromRgb(158, 183, 209));
        var labelBrush = new SolidColorBrush(Color.FromRgb(224, 235, 247));

        for (double x = Math.Ceiling(left / tickStep) * tickStep; x <= right + .00001; x += tickStep)
        {
            double pixel = DesignOffset.X + x * _zoom;
            bool labeled = AtMultiple(x, labelStep);
            var line = new Line { X1 = pixel, X2 = pixel, Y1 = labeled ? 13 : 21, Y2 = 29,
                Stroke = tickBrush, StrokeThickness = 1, IsHitTestVisible = false };
            TopRuler.Children.Add(line);
            if (!labeled || pixel < 36 || pixel > Viewport.ActualWidth - 24) continue;
            var text = new TextBlock { Text = x.ToString("0.##", CultureInfo.CurrentCulture),
                Foreground = labelBrush, FontSize = 10, IsHitTestVisible = false };
            Canvas.SetLeft(text, pixel + 3);
            Canvas.SetTop(text, 0);
            TopRuler.Children.Add(text);
        }
        for (double y = Math.Ceiling(top / tickStep) * tickStep; y <= bottom + .00001; y += tickStep)
        {
            double pixel = DesignOffset.Y + y * _zoom;
            bool labeled = AtMultiple(y, labelStep);
            var line = new Line { X1 = labeled ? 14 : 27, X2 = 37, Y1 = pixel, Y2 = pixel,
                Stroke = tickBrush, StrokeThickness = 1, IsHitTestVisible = false };
            LeftRuler.Children.Add(line);
            if (!labeled || pixel < 30 || pixel > Viewport.ActualHeight - 16) continue;
            var text = new TextBlock { Text = y.ToString("0.##", CultureInfo.CurrentCulture),
                Foreground = labelBrush, FontSize = 10, IsHitTestVisible = false,
                RenderTransform = new RotateTransform(-90) };
            Canvas.SetLeft(text, 3);
            Canvas.SetTop(text, pixel + 8);
            LeftRuler.Children.Add(text);
        }
    }

    private void Render()
    {
        // Toolpaths are not recalculated while a shape is being moved, resized or drawn;
        // the review runs again (from cache where possible) when the gesture ends.
        bool geometryGesture = _moving || _resizing || _drawing;
        if (!geometryGesture) RefreshCamReport();
        // Never redraw the hidden machine view during CAD pointer movement.
        if (_machineView && MachineCanvas is not null) RenderMachine(refreshCam: false);
        UpdateToolButtonStates();
        DesignCanvas.Width = _document.MaterialWidth;
        DesignCanvas.Height = _document.MaterialHeight;
        DesignCanvas.Children.Clear();
        DesignCanvas.Children.Add(_gridLayer);
        RefreshAdaptiveGrid();
        foreach (var s in _document.Shapes)
        {
            Shape element;
            if (s.Kind == ShapeKind.VectorPath)
                element = new System.Windows.Shapes.Path { Data = ShapeGeometry(s) };
            else
            {
                element = s.Kind == ShapeKind.Rectangle ? new Rectangle() : new Ellipse();
                element.Width = s.Width; element.Height = s.Height;
            }
            element.Stroke = _selection.Contains(s) ? Brushes.DarkOrange : Brushes.SteelBlue;
            element.StrokeThickness = _selection.Contains(s) ? 3 / _zoom : 2 / _zoom;
            element.Fill = s.Kind == ShapeKind.VectorPath && s.Contours.Any(c => !c.Closed)
                ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(45, 45, 115, 190));
            element.Tag = s;
            if (s.Kind != ShapeKind.VectorPath)
            { Canvas.SetLeft(element, s.X); Canvas.SetTop(element, s.Y); }
            DesignCanvas.Children.Add(element);
            // All standalone shapes use the same invisible bounding rectangle,
            // regardless of whether their geometry is a rectangle, ellipse or vector path.
            if (_selection.Contains(s) && s.GroupId is null)
            {
                var frame = new Rectangle
                {
                    Width = s.Width, Height = s.Height,
                    Fill = Brushes.Transparent,
                    Stroke = new SolidColorBrush(Color.FromArgb(145, 255, 140, 0)),
                    StrokeThickness = 1 / _zoom,
                    StrokeDashArray = new DoubleCollection { 3, 3 },
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(frame, s.X);
                Canvas.SetTop(frame, s.Y);
                DesignCanvas.Children.Add(frame);

                // Four diagonal and four axis-only resize grips, all a constant
                // on-screen size. Render them after the shape so they take hit priority.
                var halfX = s.X + s.Width / 2;
                var halfY = s.Y + s.Height / 2;
                var grips = new (double X, double Y, string Side, Cursor Pointer)[]
                {
                    (s.X, s.Y, "NW", Cursors.SizeNWSE),
                    (halfX, s.Y, "N", Cursors.SizeNS),
                    (s.X + s.Width, s.Y, "NE", Cursors.SizeNESW),
                    (s.X + s.Width, halfY, "E", Cursors.SizeWE),
                    (s.X + s.Width, s.Y + s.Height, "SE", Cursors.SizeNWSE),
                    (halfX, s.Y + s.Height, "S", Cursors.SizeNS),
                    (s.X, s.Y + s.Height, "SW", Cursors.SizeNESW),
                    (s.X, halfY, "W", Cursors.SizeWE)
                };
                var gripSize = 8 / _zoom;
                foreach (var grip in grips)
                {
                    var handleElement = new Rectangle
                    {
                        Width = gripSize, Height = gripSize,
                        Fill = Brushes.White,
                        Stroke = Brushes.DarkOrange,
                        StrokeThickness = 1 / _zoom,
                        Tag = new ResizeHandle(s, grip.Side),
                        Cursor = grip.Pointer
                    };
                    Canvas.SetLeft(handleElement, grip.X - gripSize / 2);
                    Canvas.SetTop(handleElement, grip.Y - gripSize / 2);
                    DesignCanvas.Children.Add(handleElement);
                }
            }
        }
        // XY-only CAD/CAM preview. This is not G-code and does not imply valid tabs or safe moves.
        // Generating toolpaths on every MouseMove is prohibitively expensive.
        // Recalculate after MouseUp; transient drags show only their geometry.
        if (_showContourPreview && !geometryGesture)
            DrawCamResults(DesignCanvas, 0, 0);
        UpdateCamHealth();
        if (_marqueeActive)
        {
            var box = WindowRect(_marqueeStart, _marqueeEnd);
            var crossing = _marqueeEnd.X < _marqueeStart.X;
            var brush = crossing ? Color.FromRgb(42, 125, 215) : Color.FromRgb(46, 151, 91);
            var outline = new Rectangle
            {
                Width = box.Width, Height = box.Height,
                Fill = new SolidColorBrush(Color.FromArgb(38, brush.R, brush.G, brush.B)),
                Stroke = new SolidColorBrush(brush), StrokeThickness = 1.5 / _zoom,
                StrokeDashArray = crossing ? new DoubleCollection { 5, 3 } : null,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(outline, box.Left); Canvas.SetTop(outline, box.Top);
            DesignCanvas.Children.Add(outline);
        }
        if (_polyPoints.Count > 0)
        {
            var preview = new Polyline { Stroke = Brushes.DarkOrange, StrokeThickness = 2 / _zoom,
                StrokeDashArray = new DoubleCollection { 5, 3 }, IsHitTestVisible = false };
            foreach (var pt in _polyPoints) preview.Points.Add(pt);
            preview.Points.Add(_polyHover);
            DesignCanvas.Children.Add(preview);
            foreach (var pt in _polyPoints)
            {
                var marker = new Ellipse { Width = 6 / _zoom, Height = 6 / _zoom,
                    Fill = Brushes.DarkOrange, IsHitTestVisible = false };
                Canvas.SetLeft(marker, pt.X - marker.Width / 2);
                Canvas.SetTop(marker, pt.Y - marker.Height / 2);
                DesignCanvas.Children.Add(marker);
            }
        }
        SelectedLabel.Text = _selected is null ? "Inget objekt markerat" :
            _selection.Count > 1 ? $"{_selection.Count} objekt markerade (visar aktivt)" : _selected.Kind.ToString();
        XBox.Text = _selected is null ? "" : F(_selected.X);
        YBox.Text = _selected is null ? "" : F(_selected.Y);
        WidthBox.Text = _selected is null ? "" : F(_selected.Width);
        HeightBox.Text = _selected is null ? "" : F(_selected.Height);
        RefreshCamSelection();
        RefreshRulers();
    }
    private static Rect WindowRect(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private IEnumerable<DesignShape> GroupMembers(DesignShape shape)
        => shape.GroupId is Guid id
            ? _document.Shapes.Where(s => s.GroupId == id)
            : new[] { shape };

    private void SelectShape(DesignShape shape, bool toggle)
    {
        var members = GroupMembers(shape).ToList();
        if (!toggle)
        {
            _selection.Clear(); _selectionOrder.Clear();
            foreach (var member in members) { _selection.Add(member); _selectionOrder.Add(member.Id); }
        }
        else
        {
            var remove = members.All(_selection.Contains);
            foreach (var member in members)
            {
                if (remove) { _selection.Remove(member); _selectionOrder.Remove(member.Id); }
                else if (_selection.Add(member)) _selectionOrder.Add(member.Id);
            }
        }
        _selected = _selection.Contains(shape) ? shape : _document.Shapes.FirstOrDefault(_selection.Contains);
    }

    private bool WindowHits(DesignShape shape, Rect bounds, bool crossing)
    {
        if (bounds.Width < .001 || bounds.Height < .001) return false;
        var geometry = ShapeGeometry(shape);
        if (!crossing)
        {
            // Bounds containment is exact for completely-contained shapes, including curves.
            return bounds.Contains(geometry.Bounds);
        }
        var region = new RectangleGeometry(bounds);
        var filled = shape.Kind != ShapeKind.VectorPath || shape.Contours.Any(c => c.Closed);
        if (filled && geometry.FillContainsWithDetail(region) != IntersectionDetail.Empty) return true;
        var pen = new Pen(Brushes.Black, 0.02);
        return geometry.StrokeContainsWithDetail(pen, region) != IntersectionDetail.Empty;
    }

    private void CommitMarquee()
    {
        var bounds = WindowRect(_marqueeStart, _marqueeEnd);
        if (!_marqueeAdd) { _selection.Clear(); _selectionOrder.Clear(); }
        if (bounds.Width > 1 / _zoom && bounds.Height > 1 / _zoom)
        {
            var crossing = _marqueeEnd.X < _marqueeStart.X;
            // A group counts as one selection unit; any member touched by a crossing window
            // selects the full group. For containment every member must fit in the window.
            var hit = _document.Shapes.Where(s => WindowHits(s, bounds, crossing)).ToHashSet();
            var complete = _document.Shapes.Where(s => s.GroupId is Guid id &&
                _document.Shapes.Where(m => m.GroupId == id).All(hit.Contains)).ToHashSet();
            foreach (var shape in _document.Shapes)
            {
                if (!(shape.GroupId is null ? hit.Contains(shape) :
                      crossing ? _document.Shapes.Any(m => m.GroupId == shape.GroupId && hit.Contains(m)) : complete.Contains(shape)))
                    continue;
                if (_selection.Add(shape)) _selectionOrder.Add(shape.Id);
            }
            StatusText.Text = $"{_selection.Count} objekt markerade ({(crossing ? "korsande" : "inneslutande")} ruta).";
        }
        _selected = _document.Shapes.FirstOrDefault(_selection.Contains);
        _marqueeActive = false;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _selection.Clear(); _selectionOrder.Clear();
        foreach (var shape in _document.Shapes) { _selection.Add(shape); _selectionOrder.Add(shape.Id); }
        _selected = _document.Shapes.FirstOrDefault(); Render();
        StatusText.Text = $"Alla {_selection.Count} objekt markerade.";
    }

    private void Group_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count < 2) { Error("Markera minst två objekt för att skapa en grupp."); return; }
        RememberChange();
        var id = Guid.NewGuid();
        foreach (var shape in _selection) shape.GroupId = id;
        Render(); StatusText.Text = $"Grupp skapad med {_selection.Count} objekt.";
    }

    private void Ungroup_Click(object sender, RoutedEventArgs e)
    {
        var groups = _selection.Where(s => s.GroupId.HasValue).Select(s => s.GroupId).Distinct().ToHashSet();
        if (groups.Count == 0) { Error("Markera en grupp att dela upp."); return; }
        RememberChange();
        foreach (var shape in _document.Shapes.Where(s => groups.Contains(s.GroupId))) shape.GroupId = null;
        Render(); StatusText.Text = $"{groups.Count} grupp(er) upplösta.";
    }

    private void Canvas_Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(DesignCanvas);
        var sourceTag = (e.OriginalSource as FrameworkElement)?.Tag;
        var handle = sourceTag as ResizeHandle;
        var clicked = handle?.Shape ?? sourceTag as DesignShape;
        if (_tool == Tool.Polyline && (clicked is null || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            var snapped = SnapPoint(p);
            if (_polyPoints.Count >= 3 && (snapped - _polyPoints[0]).Length < 0.00001)
            { FinishPolyline(true); e.Handled = true; return; }
            if (_polyPoints.Count == 0 || (snapped - _polyPoints[^1]).Length > 0.00001)
                _polyPoints.Add(snapped);
            _polyHover = snapped;
            if (e.ClickCount >= 2 && _polyPoints.Count >= 2) FinishPolyline(false);
            else Render();
            e.Handled = true;
            return;
        }
        // Existing objects can always be selected, even if a drawing tool was active.
        // After that click, return to normal selection mode so the next drag on
        // empty canvas creates a selection window without pressing Select first.
        if (clicked is not null && _tool != Tool.Select)
            _tool = Tool.Select;
        // Ctrl/Shift + drag on empty space also selects when a shape tool is armed.
        // An explicit drawing tool otherwise takes precedence until one shape is created.
        var forcedMarquee = clicked is null &&
            (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
             Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        _gestureSaved = false;
        _drawing = false;
        if (clicked is not null || _tool == Tool.Select || forcedMarquee)
        {
            var additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (clicked is null)
            {
                // Dragging empty canvas in Select mode creates a CAD-style selection window.
                // Left-to-right = containment (green), right-to-left = crossing (blue).
                _marqueeActive = true;
                _marqueeStart = p; _marqueeEnd = p; _marqueeAdd = additive;
                if (!additive) { _selection.Clear(); _selected = null; _selectionOrder.Clear(); }
                _moving = false; _resizing = false; _start = null;
            }
            else
            {
                if (handle is null && additive)
                    SelectShape(clicked, true);
                else if (!_selection.Contains(clicked) || handle is not null)
                    SelectShape(clicked, false);
                _resizing = handle is not null && _selection.Count == 1 && clicked.GroupId is null;
                _moving = _selected is not null && !_resizing && !additive && handle is null;
                _start = additive ? null : p;
                _moveOrigins.Clear();
                foreach (var shape in _selection) _moveOrigins[shape] = new Point(shape.X, shape.Y);
                if (_selected is not null) _original = new Point(_selected.X, _selected.Y);
                _activeHandle = _resizing ? handle : null;
                if (_selected is not null) { _resizeWidth = _selected.Width; _resizeHeight = _selected.Height; }
            }
        }
        else
        {
            _drawing = true;
            RememberChange(); _gestureSaved = true;
            _start = p;
            _selected = new DesignShape { Kind = _tool == Tool.Rectangle ? ShapeKind.Rectangle : ShapeKind.Ellipse, X = Snap(p.X), Y = Snap(p.Y), Width = 1, Height = 1 };
            _document.Shapes.Add(_selected);
            _selection.Clear(); _selection.Add(_selected);
            _selectionOrder.Clear(); _selectionOrder.Add(_selected.Id);
        }
        DesignCanvas.CaptureMouse(); Render();
    }
    private void Canvas_Move(object sender, MouseEventArgs e)
    {
        if (_marqueeActive)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            _marqueeEnd = e.GetPosition(DesignCanvas);
            Render(); return;
        }
        if (_tool == Tool.Polyline && _polyPoints.Count > 0 && _start is null)
        {
            _polyHover = SnapPoint(e.GetPosition(DesignCanvas));
            Render(); return;
        }
        if (_start is null || e.LeftButton != MouseButtonState.Pressed || _selected is null) return;
        var p = e.GetPosition(DesignCanvas);
        if ((_resizing || _moving) && !_gestureSaved)
        { RememberChange(); _gestureSaved = true; }
        if (_resizing && _activeHandle is not null)
        {
            var selected = _activeHandle.Shape;
            var original = _original;
            var right = original.X + _resizeWidth;
            var bottom = original.Y + _resizeHeight;
            var corner = _activeHandle.Corner;
            // Side grips only change one axis. Corner grips change both.
            // Keep the opposite side fixed, prevent inversion and avoid
            // moving outside the material or collapsing to a zero-size shape.
            const double minSize = 0.1;
            var left = corner.Contains('W')
                ? Clamp(Snap(p.X), 0, right - minSize) : original.X;
            var top = corner.Contains('N')
                ? Clamp(Snap(p.Y), 0, bottom - minSize) : original.Y;
            var newRight = corner.Contains('E')
                ? Clamp(Snap(p.X), left + minSize, _document.MaterialWidth) : right;
            var newBottom = corner.Contains('S')
                ? Clamp(Snap(p.Y), top + minSize, _document.MaterialHeight) : bottom;
            selected.X = left;
            selected.Y = top;
            selected.Width = Math.Max(minSize, newRight - left);
            selected.Height = Math.Max(minSize, newBottom - top);
        }
        else if (_moving)
        {
            var dx = Snap(p.X - _start.Value.X);
            var dy = Snap(p.Y - _start.Value.Y);
            foreach (var (shape, origin) in _moveOrigins)
            {
                shape.X = Math.Max(0, origin.X + dx);
                shape.Y = Math.Max(0, origin.Y + dy);
            }
        }
        else if (_drawing)
        {
            _selected.X = Math.Max(0, Snap(Math.Min(_start.Value.X, p.X)));
            _selected.Y = Math.Max(0, Snap(Math.Min(_start.Value.Y, p.Y)));
            _selected.Width = Math.Max(1, Snap(Math.Abs(p.X - _start.Value.X)));
            _selected.Height = Math.Max(1, Snap(Math.Abs(p.Y - _start.Value.Y)));
        }
        Render();
    }
    private void Canvas_Up(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeActive)
        {
            _marqueeEnd = e.GetPosition(DesignCanvas);
            CommitMarquee();
            _start = null; _gestureSaved = false;
            DesignCanvas.ReleaseMouseCapture(); Render();
            return;
        }
        if (_drawing)
        {
            if (_start != null && _selected != null && _selected.Width <= 1 && _selected.Height <= 1)
            {
                _document.Shapes.Remove(_selected);
                _selection.Remove(_selected);
                _selectionOrder.Remove(_selected.Id);
                _selected = null;
                StatusText.Text = "Ingen form skapades. Dra på tom yta för att markera objekt.";
            }
            else
                StatusText.Text = "Form skapad. Dra på tom yta för områdesmarkering.";
            // Rectangle/Ellipse are one-shot tools. Never leave a drawing tool
            // armed after completing a shape: next empty drag means selection.
            _tool = Tool.Select;
        }
        _moving = false; _drawing = false; _resizing = false; _activeHandle = null; _start = null;
        _gestureSaved = false;
        DesignCanvas.ReleaseMouseCapture(); Render();
    }
    private void SelectTool_Click(object sender, RoutedEventArgs e) { _polyPoints.Clear(); _tool = Tool.Select; UpdateToolButtonStates(); StatusText.Text = "Verktyg: markera/flytta"; }
    private void RectangleTool_Click(object sender, RoutedEventArgs e) { _polyPoints.Clear(); _tool = Tool.Rectangle; UpdateToolButtonStates(); StatusText.Text = "Verktyg: rektangel"; }
    private void EllipseTool_Click(object sender, RoutedEventArgs e) { _polyPoints.Clear(); _tool = Tool.Ellipse; UpdateToolButtonStates(); StatusText.Text = "Verktyg: ellips"; }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0) return;
        RememberChange();
        foreach (var shape in _selection)
        { _document.Shapes.Remove(shape); _document.CamOperations.RemoveAll(o => o.ShapeId == shape.Id); }
        _selection.Clear(); _selectionOrder.Clear(); _selected = null; Render();
    }
    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_selection.Count == 0) return;
        RememberChange();
        var duplicatedGroups = new Dictionary<Guid, Guid>();
        var duplicates = _document.Shapes.Where(s => _selection.Contains(s))
            .Select(s => new DesignShape { Kind = s.Kind,
                GroupId = s.GroupId is Guid gid ? (duplicatedGroups.TryGetValue(gid, out var cloned)
                    ? cloned : duplicatedGroups[gid] = Guid.NewGuid()) : null,
                X = s.X + 10, Y = s.Y + 10,
                Width = s.Width, Height = s.Height,
                Contours = s.Contours.Select(c => new VectorContour { Closed = c.Closed,
                    Points = c.Points.Select(p => new VectorPoint(p.X, p.Y)).ToList() }).ToList() }).ToList();
        _document.Shapes.AddRange(duplicates);
        var originals = _document.Shapes.Where(x => _selection.Contains(x)).ToList();
        for (int i = 0; i < Math.Min(originals.Count, duplicates.Count); i++)
        {
            var op = _document.CamOperations.FirstOrDefault(o => o.ShapeId == originals[i].Id);
            if (op != null) _document.CamOperations.Add(new CamOperation { ShapeId = duplicates[i].Id,
                Kind = op.Kind, ToolDiameter = op.ToolDiameter, TotalDepth = op.TotalDepth,
                DepthPerPass = op.DepthPerPass, UseTabs = op.UseTabs,
                TabCount = op.TabCount, TabWidth = op.TabWidth, TabHeight = op.TabHeight });
        }
        _selection.Clear(); foreach (var item in duplicates) _selection.Add(item);
        _selected = duplicates.FirstOrDefault();
        _selectionOrder.Clear(); _selectionOrder.AddRange(duplicates.Select(d => d.Id)); Render();
    }
    // Commit only the property being edited. This prevents a redraw from overwriting
    // unrelated text boxes, and records exactly one undo step per changed value.
    private void ObjectProperty_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox field || _selected is null) return;
        CommitObjectProperty(field);
    }

    private void ObjectProperty_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox field) return;
        CommitObjectProperty(field);
        // Advance focus just like Tab after committing the current field.
        field.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    private void CommitObjectProperty(TextBox field)
    {
        if (_selected is null) return;
        var isPosition = ReferenceEquals(field, XBox) || ReferenceEquals(field, YBox);
        if (!Parse(field, out var value) || value < 0 || (!isPosition && value <= 0))
        {
            field.BorderBrush = Brushes.OrangeRed;
            field.BorderThickness = new Thickness(2);
            StatusText.Text = "Ogiltigt värde. Använd icke-negativa koordinater och positiva mått i mm.";
            return;
        }

        var oldValue = ReferenceEquals(field, XBox) ? _selected.X :
            ReferenceEquals(field, YBox) ? _selected.Y :
            ReferenceEquals(field, WidthBox) ? _selected.Width : _selected.Height;
        field.ClearValue(TextBox.BorderBrushProperty);
        field.ClearValue(TextBox.BorderThicknessProperty);
        if (Math.Abs(value - oldValue) < 0.0000001) return;

        RememberChange();
        if (ReferenceEquals(field, XBox)) _selected.X = value;
        else if (ReferenceEquals(field, YBox)) _selected.Y = value;
        else if (ReferenceEquals(field, WidthBox)) _selected.Width = value;
        else if (ReferenceEquals(field, HeightBox)) _selected.Height = value;
        Render();
        StatusText.Text = "Objektets mått/position uppdaterad. Ångra: Ctrl+Z.";
    }

    private void UpdateShape_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) { Error("Markera ett objekt först."); return; }
        if (!Parse(XBox, out var x) || !Parse(YBox, out var y) || !Parse(WidthBox, out var w) || !Parse(HeightBox, out var h) || w <= 0 || h <= 0 || x < 0 || y < 0)
        { Error("Ange giltiga positiva mått och koordinater."); return; }
        RememberChange();
        _selected.X = x; _selected.Y = y; _selected.Width = w; _selected.Height = h; Render();
    }
    private void UpdateMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (!Parse(MaterialWidthBox, out var w) || !Parse(MaterialHeightBox, out var h) || !Parse(MaterialThicknessBox, out var t) || w <= 0 || h <= 0 || t <= 0 || w > 5000 || h > 5000)
        { Error("Ogiltigt materialmått (bredd/höjd 1–5000 mm)."); return; }
        RememberChange();
        _document.Name = NameBox.Text;
        _document.MaterialWidth = w; _document.MaterialHeight = h; _document.MaterialThickness = t;
        if (MachineSetupValidation.Validate(_document) is string setupError)
        { RestoreSnapshot(_undo.Pop()); Error(setupError); return; }
        Render(); FitToMaterial();
    }

    private void OpenInlay_Click(object sender, RoutedEventArgs e)
    {
        SetPanelVisible("Cam", true);
        InlayExpander.IsExpanded = true;
    }
    private bool ParseInlay(TextBox field, string description, out double value)
    {
        if (!Parse(field, out value)) { Error("Ange giltigt värde för " + description + "."); return false; }
        return true;
    }
    private InlaySettings? ReadInlaySettings()
    {
        if (!ParseInlay(InlayAngle, "vinkel", out var angle) ||
            !ParseInlay(InlayVBitDia, "V-fräsdiameter", out var dia) ||
            !ParseInlay(InlayTip, "spetsdiameter", out var tip) ||
            !ParseInlay(InlayFemaleDepth, "honans djup", out var femaleDepth) ||
            !ParseInlay(InlayMaleDepth, "hanens djup", out var maleDepth) ||
            !ParseInlay(InlayStart, "startdjup", out var start) ||
            !ParseInlay(InlayGlue, "limspalt", out var glue) ||
            !ParseInlay(InlayClearance, "passningsspel", out var clearance) ||
            !ParseInlay(InlayCleanupDia, "planfräsdiameter", out var cleanup) ||
            !ParseInlay(InlayStepover, "överlapp", out var overlap) ||
            !ParseInlay(InlayFeed, "matning", out var feed) ||
            !ParseInlay(InlayPlunge, "Z-matning", out var plunge) ||
            !ParseInlay(InlaySafeZ, "säker Z", out var safeZ) ||
            !ParseInlay(InlayRpm, "spindelvarvtal", out var rpm)) return null;
        if (rpm != Math.Truncate(rpm) || rpm < int.MinValue || rpm > int.MaxValue)
        { Error("Spindelvarvtalet måste vara ett heltal."); return null; }
        return new InlaySettings
        {
            JobKind = (InlayJobKind)Math.Max(0, InlayKind.SelectedIndex),
            VBitAngleDegrees = angle, VBitDiameterMm = dia, VBitFlatTipDiameterMm = tip,
            FemaleDepthMm = femaleDepth, MaleDepthMm = maleDepth, MaleStartDepthMm = start,
            GlueGapMm = glue, FitClearanceMm = clearance, CleanupToolDiameterMm = cleanup,
            CleanupStepOverPercent = overlap, FeedRateMmPerMin = feed, PlungeRateMmPerMin = plunge,
            SafeZMm = safeZ, SpindleRpm = (int)rpm,
            MirrorFemaleX = InlayMirrorFemale.IsChecked == true,
            MirrorMaleX = InlayMirrorMale.IsChecked == true
        };
    }
    private void SaveInlay_Click(object sender, RoutedEventArgs e)
    {
        var settings = ReadInlaySettings();
        if (settings is null) return;
        var error = InlayValidation.GetError(settings, _document.MaterialThickness);
        if (error is not null) { Error(error); return; }
        RememberChange();
        _document.Inlay = settings;
        InlaySummary.Text = "Inlay-inställningar sparade i projektet. Ingen G-code har genererats.";
        StatusText.Text = "V-carve inlay-konfiguration sparad (ingen G-code).";
    }
    private void RemoveInlay_Click(object sender, RoutedEventArgs e)
    {
        if (_document.Inlay is null) return;
        RememberChange();
        _document.Inlay = null;
        InlaySummary.Text = "Inlay-konfiguration borttagen.";
    }
    private void RefreshInlaySettings()
    {
        if (InlayKind is null) return;
        var v = _document.Inlay ?? new InlaySettings();
        InlayKind.SelectedIndex = (int)v.JobKind;
        InlayAngle.Text = F(v.VBitAngleDegrees); InlayVBitDia.Text = F(v.VBitDiameterMm);
        InlayTip.Text = F(v.VBitFlatTipDiameterMm);
        InlayFemaleDepth.Text = F(v.FemaleDepthMm); InlayMaleDepth.Text = F(v.MaleDepthMm);
        InlayStart.Text = F(v.MaleStartDepthMm); InlayGlue.Text = F(v.GlueGapMm);
        InlayClearance.Text = F(v.FitClearanceMm); InlayCleanupDia.Text = F(v.CleanupToolDiameterMm);
        InlayStepover.Text = F(v.CleanupStepOverPercent); InlayFeed.Text = F(v.FeedRateMmPerMin);
        InlayPlunge.Text = F(v.PlungeRateMmPerMin); InlaySafeZ.Text = F(v.SafeZMm);
        InlayRpm.Text = v.SpindleRpm.ToString(CultureInfo.InvariantCulture);
        InlayMirrorFemale.IsChecked = v.MirrorFemaleX;
        InlayMirrorMale.IsChecked = v.MirrorMaleX;
        InlaySummary.Text = _document.Inlay is null ? "Inte konfigurerad – standardvärden visas." : "Konfiguration laddad från projektet. Ingen G-code genererad.";
    }

    private void ImportVector_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Vektorfiler|*.svg;*.dxf|SVG|*.svg|ASCII DXF|*.dxf", Title = "Importera vektorgeometri" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var result = VectorFileImporter.Read(dialog.FileName);
            var bounds = result.Bounds;
            if (bounds.Width > 5000 || bounds.Height > 5000)
                throw new InvalidDataException("Ritningens storlek överstiger 5000 mm. Kontrollera filens enheter.");
            int opened = result.Shapes.Count(s => s.Contours.Any(c => !c.Closed));
            var message = $"Fil: {System.IO.Path.GetFileName(dialog.FileName)}\n\n" +
                $"Konturer: {result.Shapes.Count}\nÖppna: {opened}\n" +
                $"Mått: {F(bounds.Width)} × {F(bounds.Height)} mm\n\n" +
                result.Notes + "\n\nImportera och placera ritningen med vänster övre hörn 10 × 10 mm på arbetsytan?";
            if (MessageBox.Show(this, message, "Förhandskontroll av vektorimport",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            // A single undo snapshot covers the entire import. Existing geometry remains unchanged.
            RememberChange();
            double dx = 10 - bounds.X, dy = 10 - bounds.Y;
            foreach (var s in result.Shapes) { s.X += dx; s.Y += dy; _document.Shapes.Add(s); }
            _selection.Clear(); _selectionOrder.Clear();
            foreach (var sh in result.Shapes) { _selection.Add(sh); _selectionOrder.Add(sh.Id); }
            _selected = result.Shapes[0];
            _tool = Tool.Select;
            _polyPoints.Clear();
            Render();
            bool outside = bounds.Width + 10 > _document.MaterialWidth || bounds.Height + 10 > _document.MaterialHeight;
            StatusText.Text = $"Importerat {result.Shapes.Count} konturer från {System.IO.Path.GetFileName(dialog.FileName)}." +
                (outside ? " VARNING: en del av geometrin ligger utanför materialet." : "");
            if (outside) Error("Importen är genomförd, men geometrin ligger delvis utanför materialet. Justera materialstorlek eller placering innan bearbetning.");
        }
        catch (Exception ex) { Error("Importen avbröts: " + ex.Message); }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    { _polyPoints.Clear(); _undo.Clear(); _redo.Clear(); _document = new CncDocument(); _camPlanner.Clear(); _selected = null; _selection.Clear(); _selectionOrder.Clear(); _path = null; SyncMetadata(); Render(); FitToMaterial(); }
    private void SyncMetadata()
    { NameBox.Text = _document.Name; MaterialWidthBox.Text = F(_document.MaterialWidth); MaterialHeightBox.Text = F(_document.MaterialHeight); MaterialThicknessBox.Text = F(_document.MaterialThickness);
      RefreshMachineSummary();
      StockXBox.Text = F(_document.Stock.X); StockYBox.Text = F(_document.Stock.Y);
      RefreshFixtureFields(); RefreshInlaySettings(); RefreshMotionFields(); }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Parse(MaterialWidthBox, out var w) || !Parse(MaterialHeightBox, out var h) || !Parse(MaterialThicknessBox, out var t) || w <= 0 || h <= 0 || t <= 0 || w > 5000 || h > 5000) { Error("Kontrollera materialmåtten innan du sparar."); return; }
        if (_document.CamOperations.Any(o => CamValidation.GetError(o, t) is not null))
        { Error("En CAM-operation är inte giltig för aktuell materialtjocklek. Kontrollera CAM-inställningarna."); return; }
        if (_document.Inlay is not null && InlayValidation.GetError(_document.Inlay, t) is string inlayError)
        { Error("Inlay-inställningarna passar inte materialtjockleken: " + inlayError); return; }
        _document.Name = NameBox.Text; _document.MaterialWidth = w; _document.MaterialHeight = h; _document.MaterialThickness = t;
        if (MachineSetupValidation.Validate(_document) is string setupError) { Error(setupError); return; }
        if (_path is null) { var dialog = new SaveFileDialog { Filter = "CNC Studio JSON|*.cncstudio.json", FileName = "projekt.cncstudio.json" }; if (dialog.ShowDialog(this) != true) return; _path = dialog.FileName; }
        try { DocumentStorage.Save(_path, _document); StatusText.Text = "Sparat: " + _path; }
        catch (Exception ex) { Error("Kunde inte spara: " + ex.Message); }
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "CNC Studio JSON|*.cncstudio.json|JSON|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { var document = DocumentStorage.Load(dialog.FileName); _document = document; _camPlanner.Clear(); _path = dialog.FileName; _selected = null; _selection.Clear(); _selectionOrder.Clear(); _undo.Clear(); _redo.Clear(); _polyPoints.Clear(); SyncMetadata(); Render(); FitToMaterial(); StatusText.Text = "Öppnat: " + _path; }
        catch (Exception ex) { Error("Kunde inte öppna: " + ex.Message); }
    }
    private void UpdatePositions()
    { XPosition.Text = _position['X'].ToString("0.000", CultureInfo.InvariantCulture); YPosition.Text = _position['Y'].ToString("0.000", CultureInfo.InvariantCulture); ZPosition.Text = _position['Z'].ToString("0.000", CultureInfo.InvariantCulture); }
    private void ZeroX_Click(object sender, RoutedEventArgs e) { _position['X'] = 0; UpdatePositions(); }
    private void ZeroY_Click(object sender, RoutedEventArgs e) { _position['Y'] = 0; UpdatePositions(); }
    private void ZeroZ_Click(object sender, RoutedEventArgs e) { _position['Z'] = 0; UpdatePositions(); }
    private void ZeroXY_Click(object sender, RoutedEventArgs e) { _position['X'] = 0; _position['Y'] = 0; UpdatePositions(); }
    private void SetPosition_Click(object sender, RoutedEventArgs e)
    {
        if (!Parse(SetPositionBox, out var value)) { Error("Ogiltig koordinat."); return; }
        var axis = ((AxisPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "X")[0];
        _position[axis] = value; UpdatePositions();
    }
    private void Jog_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag?.ToString() ?? "";
        if (tag.Length != 2) return;
        var axis = tag[0];
        var stepText = (StepPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1";
        if (!double.TryParse(stepText, NumberStyles.Float, CultureInfo.InvariantCulture, out var step)) return;
        _position[axis] += tag[1] == '+' ? step : -step;
        UpdatePositions(); StatusText.Text = $"DEMOLÄGE: Jog {tag} {step} mm – ingen maskin är ansluten";
    }
    private void CamPanel_Click(object sender, RoutedEventArgs e) => SetPanelVisible("Cam", !_camVisible);
    private void RefreshCamSelection()
    {
        if (CamSelectionLabel is null || CamKind is null) return;
        _loadingCam = true;
        try
        {
            CamSelectionLabel.Text = _selected is null ? "Markera ett objekt för att tilldela CAM."
                : $"Markerat: {_selected.Kind} · {_selected.Width:0.###} × {_selected.Height:0.###} mm";
            var operation = _selected is null ? null : _document.CamOperations.FirstOrDefault(o => o.ShapeId == _selected.Id);
            CamKind.SelectedIndex = operation is null ? 0 : (int)operation.Kind;
            CamDiameter.Text = F(operation?.ToolDiameter ?? 3.175);
            CamDepth.Text = F(operation?.TotalDepth ?? 6);
            CamPass.Text = F(operation?.DepthPerPass ?? 2);
            CamTabs.IsChecked = operation?.UseTabs ?? true;
            var defaults = new CamOperation();
            CamTabCount.Text = (operation?.TabCount ?? defaults.TabCount).ToString(CultureInfo.InvariantCulture);
            CamTabWidth.Text = F(operation?.TabWidth ?? defaults.TabWidth);
            CamTabHeight.Text = F(operation?.TabHeight ?? defaults.TabHeight);
            TabSettingsPanel.Visibility = CamTabs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _loadingCam = false; }
        UpdateCamSummary();
    }
    private void CamInput_Changed(object sender, RoutedEventArgs e)
    {
        if (TabSettingsPanel is not null && CamTabs is not null)
            TabSettingsPanel.Visibility = CamTabs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (!_loadingCam) UpdateCamSummary();
    }
    private CamOperation? ReadCam()
    {
        if (_selected is null || CamKind is null || CamDiameter is null || CamDepth is null || CamPass is null)
            return null;
        if (!Parse(CamDiameter, out var diameter) || !Parse(CamDepth, out var depth) || !Parse(CamPass, out var step))
            return null;
        bool useTabs = CamTabs.IsChecked == true;
        var defaults = new CamOperation();
        int tabCount = defaults.TabCount; double tabWidth = defaults.TabWidth, tabHeight = defaults.TabHeight;
        if (CamTabCount is not null && CamTabWidth is not null && CamTabHeight is not null)
        {
            bool okCount = int.TryParse(CamTabCount.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c);
            bool okWidth = Parse(CamTabWidth, out var w), okHeight = Parse(CamTabHeight, out var h);
            // Tab fields only need to be valid when tabs are used.
            if (useTabs && !(okCount && okWidth && okHeight)) return null;
            if (okCount) tabCount = c;
            if (okWidth) tabWidth = w;
            if (okHeight) tabHeight = h;
        }
        return new CamOperation { ShapeId = _selected.Id,
            Kind = (CamOperationKind)Math.Max(0, CamKind.SelectedIndex), ToolDiameter = diameter,
            TotalDepth = depth, DepthPerPass = step, UseTabs = useTabs,
            TabCount = tabCount, TabWidth = tabWidth, TabHeight = tabHeight };
    }
    private void UpdateCamSummary()
    {
        if (CamSummary is null) return;
        var op = ReadCam();
        if (op is null) { CamSummary.Text = "Markera ett objekt och ange numeriska värden."; return; }
        var error = CamValidation.GetError(op, _document.MaterialThickness);
        CamSummary.Text = error ?? $"{op.Kind} · Ø {F(op.ToolDiameter)} mm · {F(op.TotalDepth)} mm djup · " +
            $"{Math.Ceiling(op.TotalDepth / op.DepthPerPass):0} pass · Hållflikar: " +
            (!op.UseTabs ? "Nej"
             : op.TotalDepth >= _document.MaterialThickness - 1e-6
                ? $"{op.TabCount} st, {F(op.TabWidth)} × {F(op.TabHeight)} mm"
                : "används först vid genomskärning");
    }
    private void SaveCam_Click(object sender, RoutedEventArgs e)
    {
        var op = ReadCam();
        if (op is null) { Error("Markera ett objekt och ange giltiga CAM-värden."); return; }
        var err = CamValidation.GetError(op, _document.MaterialThickness);
        if (err != null) { Error(err); return; }
        var target = _document.Shapes.FirstOrDefault(sh => sh.Id == op.ShapeId);
        if (target?.Kind == ShapeKind.VectorPath && target.Contours.Any(c => !c.Closed))
        { Error("Öppna polylinjer kan inte få area-baserade CAM-operationer. Stäng konturen först."); return; }
        RememberChange();
        _document.CamOperations.RemoveAll(x => x.ShapeId == op.ShapeId);
        _document.CamOperations.Add(op);
        _showContourPreview = true;
        if (ContourVisibilityCheck is not null) ContourVisibilityCheck.IsChecked = true;
        // A newly saved operation is always computed at once, even for heavy shapes.
        _camReport = _camPlanner.Review(_document, computeShapes: new[] { op.ShapeId });
        Render();
        StatusText.Text = "CAM sparad. Grön streckad XY-bana visas direkt (ej körbar G-code).";
    }
    private void ToggleContourPreview_Click(object sender, RoutedEventArgs e)
    {
        _showContourPreview = !_showContourPreview;
        if (ContourVisibilityCheck is not null) ContourVisibilityCheck.IsChecked = _showContourPreview;
        Render();
        StatusText.Text = _showContourPreview ? "XY-verktygsbanor synliga (ej körbar G-code)." : "XY-verktygsbanor dolda.";
    }

    private void ContourVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (ContourVisibilityCheck is null) return; // XAML initialization
        _showContourPreview = ContourVisibilityCheck.IsChecked == true;
        if (DesignCanvas is not null && MachineCanvas is not null) Render();
    }

    private void RemoveCam_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        RememberChange();
        _document.CamOperations.RemoveAll(o => o.ShapeId == _selected.Id);
        RefreshCamSelection();
        StatusText.Text = "CAM-inställningar borttagna från objektet.";
        Render();
    }

}
