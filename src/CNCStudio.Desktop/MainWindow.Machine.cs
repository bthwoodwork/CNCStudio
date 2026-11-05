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
    private static readonly Brush StockFill = new SolidColorBrush(Color.FromRgb(222, 194, 149));
    private static readonly Brush MachineGridStroke = new SolidColorBrush(Color.FromArgb(70, 88, 116, 139));
    private static readonly Brush FixtureOutline = new SolidColorBrush(Color.FromRgb(126, 56, 25));
    private readonly Canvas _machineGridLayer = new() { IsHitTestVisible = false };
    private (double W, double H, double Zoom, double Step)? _machineGridCache;

    private void MachineView_Click(object sender, RoutedEventArgs e) => SetMachineView(true);
    private void StockView_Click(object sender, RoutedEventArgs e) => SetMachineView(false);

    private void SetMachineView(bool machine)
    {
        _machineView = machine;
        DesignCanvas.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        MachineCanvas.Visibility = machine ? Visibility.Visible : Visibility.Collapsed;
        TopRuler.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        LeftRuler.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        SetFixtureDrawing(false);
        Render();
        Dispatcher.BeginInvoke(new Action(FitToMaterial), System.Windows.Threading.DispatcherPriority.Loaded);
        StatusText.Text = machine
            ? "MASKINVY: Planerad materialplacering. Inte kalibrerad mot GRBL; ingen maskin körs."
            : "MATERIALVY: Redigera designen och dess CAM-inställningar.";
    }

    private void RenderMachine()
    {
        if (MachineCanvas is null) return;
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
        for (double x = 0; x <= w && x / gridStep < 3000; x += gridStep)
        {
            var line = new Line { X1 = x, Y1 = 0, X2 = x, Y2 = h,
                Stroke = MachineGridStroke, StrokeThickness = 0.7 / _zoom,
                IsHitTestVisible = false };
            _machineGridLayer.Children.Add(line);
        }
        for (double y = 0; y <= h && y / gridStep < 3000; y += gridStep)
        {
            var line = new Line { X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = MachineGridStroke, StrokeThickness = 0.7 / _zoom,
                IsHitTestVisible = false };
            _machineGridLayer.Children.Add(line);
        }
        }
        MachineCanvas.Children.Add(_machineGridLayer);

        var stock = new Rectangle { Width = _document.MaterialWidth, Height = _document.MaterialHeight,
            Fill = StockFill, Stroke = Brushes.SaddleBrown, StrokeThickness = 2 / _zoom,
            Cursor = Cursors.SizeAll, Tag = "stock", ToolTip = "Dra materialet eller ange X/Y i egenskaper" };
        Canvas.SetLeft(stock, _document.Stock.X); Canvas.SetTop(stock, _document.Stock.Y);
        MachineCanvas.Children.Add(stock);

        foreach (var sh in _document.Shapes)
        {
            var shape = new System.Windows.Shapes.Path { Data = ShapeGeometry(sh),
                Stroke = Brushes.SteelBlue, StrokeThickness = 1.5 / _zoom,
                Fill = new SolidColorBrush(Color.FromArgb(35, 50, 138, 201)),
                IsHitTestVisible = false };
            shape.RenderTransform = new TranslateTransform(_document.Stock.X, _document.Stock.Y);
            MachineCanvas.Children.Add(shape);
        }

        // Render the saved CAM XY centerlines in machine coordinates as well.
        // This is a VISUAL PREVIEW only: no Z routing, tabs, holder collision or executable G-code.
        // Avoid expensive regeneration during dragging, just as the design view does.
        if (_showContourPreview && _machineDragStart is null)
        {
            foreach (var op in _document.CamOperations)
            {
                var target = _document.Shapes.FirstOrDefault(sh => sh.Id == op.ShapeId);
                if (target is null) continue;
                try
                {
                    var preview = VectorContourPreview.Generate(target, op, _document.MaterialThickness);
                    var problems = CamSafetyReview.Analyze(_document, target, op, preview);
                    for (int pathIndex = 0; pathIndex < preview.Centerlines.Count; pathIndex++)
                    {
                        var centerline = preview.Centerlines[pathIndex];
                        var line = new Polyline { Stroke = Brushes.LimeGreen,
                            StrokeThickness = 2.4 / _zoom, IsHitTestVisible = false,
                            StrokeDashArray = new DoubleCollection { 5, 2 } };
                        foreach (var point in centerline)
                            line.Points.Add(new Point(point.X + _document.Stock.X, point.Y + _document.Stock.Y));
                        MachineCanvas.Children.Add(line);
                    }
                    AddConflictMarks(MachineCanvas, preview, problems, _document.Stock.X, _document.Stock.Y);
                }
                catch (Exception) { /* Unsupported paths must not be shown as verified. */ }
            }
        }

        foreach (var f in _document.Fixtures)
        {
            var block = new Rectangle { Width = f.Width, Height = f.Length,
                Fill = _fixtureBrush,
                Stroke = _selectedFixture?.Id == f.Id ? Brushes.Yellow : FixtureOutline,
                StrokeThickness = (_selectedFixture?.Id == f.Id ? 3 : 1.5) / _zoom,
                Tag = f, Cursor = f.Locked ? Cursors.Arrow : Cursors.SizeAll,
                ToolTip = $"{f.Name}: {f.HeightAboveStockMm:0.##} mm över material, marginal {f.ClearanceMm:0.##} mm" };
            Canvas.SetLeft(block, f.X); Canvas.SetTop(block, f.Y);
            MachineCanvas.Children.Add(block);
            AddFixtureLabel(f);
        }
        // Deliberately do not show a 'live' toolhead here. Homing/GRBL mapping is not implemented.
    }

    private void SaveMachineSetup_Click(object sender, RoutedEventArgs e)
    {
        if (!Parse(MachineWidthBox, out var w) || !Parse(MachineLengthBox, out var h) ||
            !Parse(StockXBox, out var x) || !Parse(StockYBox, out var y))
        { Error("Ange giltiga maskin- och materialkoordinater."); return; }
        var candidate = new MachineProfile { WorkWidthMm = w, WorkLengthMm = h,
            Name = _document.Machine.Name, CalibratedToGrbl = false };
        var oldMachine = _document.Machine; var oldStock = _document.Stock;
        _document.Machine = candidate; _document.Stock = new StockPlacement { X = x, Y = y };
        var error = MachineSetupValidation.Validate(_document);
        _document.Machine = oldMachine; _document.Stock = oldStock;
        if (error is not null) { Error(error); return; }
        RememberChange(); _document.Machine = candidate; _document.Stock = new StockPlacement { X = x, Y = y };
        _selectedFixture = null; Render(); FitToMaterial();
        StatusText.Text = "Maskin-/materialplacering sparad i projektet (ej GRBL-kalibrerad).";
    }

    private void StartFixture_Click(object sender, RoutedEventArgs e)
    {
        SetMachineView(true);
        SetFixtureDrawing(true);
        SetPanelVisible("Properties", true);
        StatusText.Text = "FASTSPÄNNING AKTIV: Dra en rektangel på maskinbordet. Ange höjd i Egenskaper. Esc avbryter.";
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
            text.Foreground = Brushes.DarkRed;
            text.Background = new SolidColorBrush(Color.FromArgb(215, 255, 248, 236));
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
        FixtureSelectionLabel.Text = f is null ? "Inget hinder valt" : f.Name;
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
        if (_selectedFixture is null) { Error("Välj ett hinder i maskinvyn först."); return; }
        if (!Parse(FixtureXBox, out var x) || !Parse(FixtureYBox, out var y) ||
            !Parse(FixtureWidthBox, out var width) || !Parse(FixtureLengthBox, out var length) ||
            !Parse(FixtureHeightBox, out var height) || !Parse(FixtureClearanceBox, out var clearance))
        { Error("Ogiltiga mått för fastspänningen."); return; }
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
                Stroke = Brushes.DarkRed, StrokeThickness = 2 / _zoom,
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

    /// <summary>
    /// Draws only the conflicting part (TStart..TEnd) of each flagged segment in red.
    /// offsetX/Y converts stock-local path coordinates to the target canvas.
    /// </summary>
    private void AddConflictMarks(Canvas canvas, ContourPreview preview,
        IReadOnlyList<CamSafetyReview.Issue> issues, double offsetX, double offsetY)
    {
        var paths = preview.Centerlines.Count > 0 ? preview.Centerlines :
            new IReadOnlyList<CamPoint>[] { preview.Centerline };
        foreach (var issue in issues)
        {
            if (issue.PathIndex < 0 || issue.PathIndex >= paths.Count) continue;
            var path = paths[issue.PathIndex];
            if (issue.SegmentIndex < 0 || issue.SegmentIndex + 1 >= path.Count) continue;
            var a = path[issue.SegmentIndex]; var b = path[issue.SegmentIndex + 1];
            double t0 = Math.Clamp(issue.TStart, 0, 1), t1 = Math.Clamp(issue.TEnd, 0, 1);
            canvas.Children.Add(new Line {
                X1 = a.X + (b.X - a.X) * t0 + offsetX, Y1 = a.Y + (b.Y - a.Y) * t0 + offsetY,
                X2 = a.X + (b.X - a.X) * t1 + offsetX, Y2 = a.Y + (b.Y - a.Y) * t1 + offsetY,
                Stroke = Brushes.Red, StrokeThickness = 3.5 / _zoom,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false });
        }
    }
}
