using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

public partial class MainWindow
{
    private static readonly Brush StockFill = CanvasTheme.MachineStock;
    private static readonly Brush MachineGridStroke = CanvasTheme.MachineGrid;
    private static readonly Brush FixtureOutline = CanvasTheme.FixtureEdge;
    private readonly Canvas _machineGridLayer = new() { IsHitTestVisible = false };
    private (double W, double H, double Zoom, double Step)? _machineGridCache;

    private void SetMachineView(bool machine)
    {
        _machineView = machine;
        DesignCanvas.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        MachineCanvas.Visibility = machine ? Visibility.Visible : Visibility.Collapsed;
        TopRuler.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        LeftRuler.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        RulerCorner.Visibility = LeftRuler.Visibility;
        SetFixtureDrawing(false);
        Render();
        Dispatcher.BeginInvoke(new Action(FitToMaterial), System.Windows.Threading.DispatcherPriority.Loaded);
        StatusText.Text = machine
            ? "MASKINVY: Planerad materialplacering. Inte kalibrerad mot GRBL; ingen maskin körs."
            : "MATERIALVY: Redigera designen och dess CAM-inställningar.";
    }

    private void RenderMachine(bool refreshCam = true)
    {
        if (MachineCanvas is null) return;
        // Moving stock or fixtures never changes a stock-local toolpath, so this is a cache
        // hit plus the XY review: conflicts update live while a fixture is dragged.
        if (refreshCam) { RefreshCamReport(); UpdateCamHealth(); }
        MachineCanvas.Children.Clear();
        double w = _document.Machine.WorkWidthMm, h = _document.Machine.WorkLengthMm;
        MachineCanvas.Width = w; MachineCanvas.Height = h;
        var gridStep = GridStep(_zoom);
        var gridKey = (w, h, _zoom, gridStep);
        if (!_machineGridCache.HasValue || _machineGridCache.Value != gridKey)
        {
        _machineGridCache = gridKey;
        _machineGridLayer.Children.Clear();
        _machineGridLayer.Width = w;
        _machineGridLayer.Height = h;
        // A machine table is large: 10 mm minor lines, stronger every 50 and 100 mm.
        double step = Math.Max(gridStep, 10);
        var c = CanvasTheme.GridColor;
        Brush GridBrush(double v) => new SolidColorBrush(Color.FromArgb(
            AtMultiple(v, 100) ? (byte)75 : AtMultiple(v, 50) ? (byte)45 : (byte)18, c.R, c.G, c.B));
        for (double x = 0; x <= w && x / step < 3000; x += step)
            _machineGridLayer.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = h,
                Stroke = GridBrush(x), StrokeThickness = 0.8 / _zoom, IsHitTestVisible = false });
        for (double y = 0; y <= h && y / step < 3000; y += step)
            _machineGridLayer.Children.Add(new Line { X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = GridBrush(y), StrokeThickness = 0.8 / _zoom, IsHitTestVisible = false });
        _machineGridLayer.Children.Add(new Rectangle { Width = w, Height = h, Stroke = CanvasTheme.MachineBedEdge,
            StrokeThickness = 1.2 / _zoom, IsHitTestVisible = false });
        }
        MachineCanvas.Children.Add(_machineGridLayer);

        var stock = new Rectangle { Width = _document.MaterialWidth, Height = _document.MaterialHeight,
            Fill = StockFill, Stroke = CanvasTheme.StockEdge, StrokeThickness = 1.2 / _zoom,
            Cursor = Cursors.SizeAll, Tag = "stock", ToolTip = "Dra materialet eller ange X/Y i egenskaper" };
        Canvas.SetLeft(stock, _document.Stock.X); Canvas.SetTop(stock, _document.Stock.Y);
        MachineCanvas.Children.Add(stock);

        foreach (var sh in _document.Shapes)
        {
            var shape = new System.Windows.Shapes.Path { Data = ShapeGeometry(sh),
                Stroke = CanvasTheme.ShapeStroke, StrokeThickness = 1.1 / _zoom,
                Fill = CanvasTheme.ShapeFill,
                IsHitTestVisible = false };
            shape.RenderTransform = new TranslateTransform(_document.Stock.X, _document.Stock.Y);
            MachineCanvas.Children.Add(shape);
        }

        // Render the saved CAM XY centerlines in machine coordinates as well.
        // This is a VISUAL PREVIEW only: no Z routing, tabs, holder collision or executable G-code.
        if (_showContourPreview)
            DrawCamResults(MachineCanvas, _document.Stock.X, _document.Stock.Y);
        DrawWorkZeroMarker(MachineCanvas, _document.Stock.X, _document.Stock.Y);

        foreach (var f in _document.Fixtures)
        {
            var block = new Rectangle { Width = f.Width, Height = f.Length,
                Fill = CanvasTheme.Fixture,
                Stroke = _selectedFixture?.Id == f.Id ? CanvasTheme.FixtureSelected : FixtureOutline,
                StrokeThickness = (_selectedFixture?.Id == f.Id ? 2 : 1.1) / _zoom,
                Tag = f, Cursor = f.Locked ? Cursors.Arrow : Cursors.SizeAll,
                ToolTip = $"{f.Name}: {f.HeightAboveStockMm:0.##} mm över material, marginal {f.ClearanceMm:0.##} mm" };
            Canvas.SetLeft(block, f.X); Canvas.SetTop(block, f.Y);
            MachineCanvas.Children.Add(block);
            AddFixtureLabel(f);
        }
        AddHomeMarker();
        // Deliberately do not show a 'live' toolhead here. Homing/GRBL mapping is not implemented.
    }

    /// <summary>Marks the X/Y homing corner (top of the view = back of the machine).</summary>
    private void AddHomeMarker()
    {
        var m = _document.Machine;
        bool right = m.HomeCorner is HomeCorner.BackRight or HomeCorner.FrontRight;
        bool front = m.HomeCorner is HomeCorner.FrontLeft or HomeCorner.FrontRight;
        double size = 14 / _zoom;
        var dot = new Ellipse { Width = size, Height = size, Fill = CanvasTheme.AxisY,
            Stroke = CanvasTheme.GripFill, StrokeThickness = 1.5 / _zoom, IsHitTestVisible = false };
        double cx = right ? m.WorkWidthMm : 0, cy = front ? m.WorkLengthMm : 0;
        Canvas.SetLeft(dot, cx - size / 2); Canvas.SetTop(dot, cy - size / 2);
        MachineCanvas.Children.Add(dot);
        var label = new TextBlock { Text = "HEM", FontSize = 11 / _zoom, FontWeight = FontWeights.Bold,
            Foreground = CanvasTheme.AxisY, IsHitTestVisible = false };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, right ? cx - label.DesiredSize.Width - size : cx + size);
        Canvas.SetTop(label, front ? cy - label.DesiredSize.Height - size / 2 : cy + size / 2);
        MachineCanvas.Children.Add(label);
    }

    private void SaveMachineSetup_Click(object sender, RoutedEventArgs e)
    {
        if (!Parse(StockXBox, out var x) || !Parse(StockYBox, out var y))
        { Error("Ange giltiga materialkoordinater (X och Y i mm).", StockXBox); return; }
        var oldStock = _document.Stock;
        _document.Stock = new StockPlacement { X = x, Y = y };
        var error = MachineSetupValidation.Validate(_document);
        _document.Stock = oldStock;
        if (error is not null) { Error(error); return; }
        RememberChange(); _document.Stock = new StockPlacement { X = x, Y = y };
        _selectedFixture = null; Render(); FitToMaterial();
        StatusText.Text = "Materialplacering sparad i projektet (ej GRBL-kalibrerad).";
    }

    private void MachineSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new MachineSettingsWindow(_document.Machine, d =>
        {
            // Validate the candidate against the current stock and fixtures before accepting.
            var old = _document.Machine;
            _document.Machine = d;
            var error = MachineSetupValidation.Validate(_document);
            _document.Machine = old;
            return error;
        }, Grbl) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        RememberChange();
        _document.Machine = dialog.Result;
        RefreshMachineSummary(); RefreshMotionFields();
        _selectedFixture = null; Render();
        StatusText.Text = $"Maskininställningar sparade: {dialog.Result.Name} (ej GRBL-kalibrerad).";
    }

    private void RefreshMachineSummary()
    {
        if (MachineSummaryText is null) return;
        var m = _document.Machine;
        string corner = m.HomeCorner switch
        {
            HomeCorner.BackLeft => "bak vänster", HomeCorner.BackRight => "bak höger",
            HomeCorner.FrontLeft => "fram vänster", _ => "fram höger"
        };
        MachineSummaryText.Text = $"{m.Name}\nX {F(m.WorkWidthMm)} × Y {F(m.WorkLengthMm)} × Z {F(m.ZTravelMm)} mm · " +
            $"hem {corner} ($23={m.HomingDirInvertMask}) · Z-brytare upptill · marginal {F(m.LimitMarginMm)} mm" +
            $"\nFräs: {(m.Spindle == SpindleControl.Manual ? "manuell (programmet pausar med M0)" : "styrs av GRBL (M3/M5)")}" +
            (m.HomeOnStartup ? "" : "\nVARNING: homing vid start är avstängd.");
    }

    private void StartFixture_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace != Workspace.Machine) SetWorkspace(Workspace.Machine);
        SetFixtureDrawing(true);
        StatusText.Text = "FASTSPÄNNING AKTIV: Dra en rektangel på maskinbordet. Ange höjd under Fastspänningar. Esc avbryter.";
    }

    /// <summary>
    /// Places the fixture name inside the block when it fits; otherwise just above
    /// the block on a light plate so it stays readable over design and toolpaths.
    /// </summary>
    private void AddFixtureLabel(FixtureBlock f)
    {
        double pad = 3 / _zoom;
        var text = new TextBlock { Text = $"{f.Name} ({f.HeightAboveStockMm:0.#} mm)",
            FontWeight = FontWeights.SemiBold, FontSize = 12 / _zoom,
            Padding = new Thickness(pad, pad / 2, pad, pad / 2), IsHitTestVisible = false };
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = text.DesiredSize;
        if (size.Width <= f.Width && size.Height <= f.Length)
        {
            text.Foreground = Brushes.White;
            Canvas.SetLeft(text, f.X); Canvas.SetTop(text, f.Y);
        }
        else
        {
            text.Foreground = CanvasTheme.FixtureEdge;
            text.Background = new SolidColorBrush(Color.FromArgb(225, 0x10, 0x1B, 0x27));
            Canvas.SetLeft(text, f.X);
            // Above the block; below it if the block sits at the top edge of the table.
            double top = f.Y - size.Height - pad;
            Canvas.SetTop(text, top >= 0 ? top : f.Y + f.Length + pad);
        }
        MachineCanvas.Children.Add(text);
    }

    /// <summary>
    /// Turns fixture drawing mode on or off: flag, button colour and cursor together.
    /// While drawing, a thin crosshair is forced over the whole machine table so the
    /// material's and fixtures' "move" cursor (four-way arrow) does not suggest
    /// that a drag will move them.
    /// </summary>
    private void SetFixtureDrawing(bool on)
    {
        _fixtureDrawing = on;
        if (DrawFixtureButton is not null)
            DrawFixtureButton.Background = on
                ? new SolidColorBrush(Color.FromRgb(44, 132, 80))
                : new SolidColorBrush(Color.FromRgb(150, 82, 42));
        if (MachineCanvas is not null)
        {
            MachineCanvas.Cursor = on ? Cursors.Cross : null;
            MachineCanvas.ForceCursor = on;
        }
    }

    private void RefreshFixtureFields()
    {
        if (FixtureSelectionLabel is null) return;
        var f = _selectedFixture;
        FixtureSelectionLabel.Text = f is null ? "Ingen vald – klicka på en på bordet" : f.Name;
        if (f is null) return;
        FixtureNameBox.Text = f.Name;
        FixtureXBox.Text = F(f.X); FixtureYBox.Text = F(f.Y);
        FixtureWidthBox.Text = F(f.Width); FixtureLengthBox.Text = F(f.Length);
        FixtureHeightBox.Text = F(f.HeightAboveStockMm);
        FixtureClearanceBox.Text = F(f.ClearanceMm);
        FixtureLockedBox.IsChecked = f.Locked;
    }

    private void SaveFixture_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFixture is null) { Error("Klicka på en fastspänning på maskinbordet först."); return; }
        if (!Parse(FixtureXBox, out var x) || !Parse(FixtureYBox, out var y) ||
            !Parse(FixtureWidthBox, out var width) || !Parse(FixtureLengthBox, out var length) ||
            !Parse(FixtureHeightBox, out var height) || !Parse(FixtureClearanceBox, out var clearance))
        { Error("Ogiltiga mått för fastspänningen – alla fält ska innehålla tal i mm.", FixtureWidthBox); return; }
        var f = _selectedFixture;
        var clone = new FixtureBlock { Id = f.Id, Name = FixtureNameBox.Text,
            X = x, Y = y, Width = width, Length = length,
            HeightAboveStockMm = height, ClearanceMm = clearance,
            Locked = FixtureLockedBox.IsChecked == true };
        var index = _document.Fixtures.IndexOf(f);
        if (index < 0) return;
        _document.Fixtures[index] = clone;
        var error = MachineSetupValidation.Validate(_document);
        _document.Fixtures[index] = f;
        if (error is not null) { Error(error); return; }
        RememberChange(); _document.Fixtures[index] = clone; _selectedFixture = clone;
        RefreshFixtureFields(); Render();
    }

    private void DeleteFixture_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFixture is null) return;
        RememberChange(); _document.Fixtures.Remove(_selectedFixture); _selectedFixture = null;
        RefreshFixtureFields(); Render();
    }

    private void Machine_Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(MachineCanvas);
        var source = (e.OriginalSource as FrameworkElement)?.Tag;
        _machineDragStart = p;
        _movingFixture = false; _movingStock = false;
        if (_fixtureDrawing)
        {
            MachineCanvas.CaptureMouse(); e.Handled = true; return;
        }
        if (source is FixtureBlock f)
        {
            _selectedFixture = f; RefreshFixtureFields();
            if (!f.Locked) { _movingFixture = true; _machineDragOriginal = new Point(f.X, f.Y); MachineCanvas.CaptureMouse(); }
        }
        else if (source is string tag && tag == "stock")
        {
            _movingStock = true; _machineDragOriginal = new Point(_document.Stock.X, _document.Stock.Y);
            MachineCanvas.CaptureMouse(); _selectedFixture = null;
            RefreshFixtureFields();
        }
        else _machineDragStart = null;
        RenderMachine(); e.Handled = true;
    }

    private void Machine_Move(object sender, MouseEventArgs e)
    {
        if (_machineDragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(MachineCanvas);
        var dx = point.X - start.X; var dy = point.Y - start.Y;
        if (_movingStock)
        {
            _document.Stock.X = Clamp(_machineDragOriginal.X + dx, 0,
                Math.Max(0, _document.Machine.WorkWidthMm - _document.MaterialWidth));
            _document.Stock.Y = Clamp(_machineDragOriginal.Y + dy, 0,
                Math.Max(0, _document.Machine.WorkLengthMm - _document.MaterialHeight));
            StockXBox.Text = F(_document.Stock.X); StockYBox.Text = F(_document.Stock.Y);
            RenderMachine();
        }
        else if (_movingFixture && _selectedFixture is FixtureBlock f)
        {
            f.X = Clamp(_machineDragOriginal.X + dx, 0,
                Math.Max(0, _document.Machine.WorkWidthMm - f.Width));
            f.Y = Clamp(_machineDragOriginal.Y + dy, 0,
                Math.Max(0, _document.Machine.WorkLengthMm - f.Length));
            RefreshFixtureFields(); RenderMachine();
        }
        else if (_fixtureDrawing)
        {
            RenderMachine();
            var r = new Rect(start, point);
            var outline = new Rectangle { Width = r.Width, Height = r.Height,
                Stroke = CanvasTheme.FixtureEdge, StrokeThickness = 1.2 / _zoom,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false };
            Canvas.SetLeft(outline, r.Left); Canvas.SetTop(outline, r.Top);
            MachineCanvas.Children.Add(outline);
        }
    }

    private void Machine_Up(object sender, MouseButtonEventArgs e)
    {
        if (_machineDragStart is not Point start) return;
        var p = e.GetPosition(MachineCanvas);
        if (MachineCanvas.IsMouseCaptured) MachineCanvas.ReleaseMouseCapture();
        if (_fixtureDrawing)
        {
            SetFixtureDrawing(false);
            var r = new Rect(start, p);
            if (r.Width >= 2 && r.Height >= 2)
            {
                var f = new FixtureBlock { X = r.Left, Y = r.Top,
                    Width = r.Width, Length = r.Height };
                _document.Fixtures.Add(f);
                if (MachineSetupValidation.Validate(_document) is string error)
                { _document.Fixtures.Remove(f); Error(error); }
                else { _document.Fixtures.Remove(f); RememberChange(); _document.Fixtures.Add(f);
                    _selectedFixture = f; RefreshFixtureFields(); }
            }
        }
        else if (_movingStock || _movingFixture)
        {
            double movedX = _movingStock ? _document.Stock.X : _selectedFixture?.X ?? 0;
            double movedY = _movingStock ? _document.Stock.Y : _selectedFixture?.Y ?? 0;
            if (Math.Abs(movedX - _machineDragOriginal.X) > .001 ||
                Math.Abs(movedY - _machineDragOriginal.Y) > .001)
            {
                // Record the state before the drag as one undo step.
                if (_movingStock) { _document.Stock.X = _machineDragOriginal.X; _document.Stock.Y = _machineDragOriginal.Y; }
                else if (_selectedFixture is FixtureBlock f) { f.X = _machineDragOriginal.X; f.Y = _machineDragOriginal.Y; }
                RememberChange();
                if (_movingStock) { _document.Stock.X = movedX; _document.Stock.Y = movedY; }
                else if (_selectedFixture is FixtureBlock f) { f.X = movedX; f.Y = movedY; }
            }
        }
        _machineDragStart = null; _movingStock = false; _movingFixture = false;
        RefreshFixtureFields(); RenderMachine();
    }

}
