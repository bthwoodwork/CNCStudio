using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// CAM preview, live validation and the CAM status panel.
/// All toolpaths come from one <see cref="CamPlanner"/> that caches them, so redraws on
/// zoom, selection or fixture moves never recompute geometry.
/// </summary>
public partial class MainWindow
{
    /// <summary>Single source for the version shown in dialogs. Keep in sync with Title in MainWindow.xaml.</summary>
    private const string AppTitle = "CNC Studio 0.9.9";

    private readonly CamPlanner _camPlanner = new(VectorContourPreview.Generate);
    private CamReport _camReport = CamReport.Empty;

    private static readonly Brush CriticalBrush = Brushes.Red;
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(255, 140, 0));
    private static readonly Brush HealthCritical = new SolidColorBrush(Color.FromRgb(255, 120, 110));
    private static readonly Brush HealthWarning = new SolidColorBrush(Color.FromRgb(255, 196, 110));
    private static readonly Brush HealthOk = new SolidColorBrush(Color.FromRgb(150, 220, 160));

    /// <summary>Cheap shapes are recomputed if changed; heavy shapes are marked stale.</summary>
    private void RefreshCamReport() => _camReport = _camPlanner.Review(_document);

    private void RefreshMotionFields()
    {
        if (TravelHeightBox is null) return;
        var m = _document.Motion;
        TravelHeightBox.Text = F(m.TravelHeightMm);
        PlungeClearanceBox.Text = F(m.PlungeClearanceMm);
        SpinUpBox.Text = F(m.SpindleSpinUpSeconds);
        RapidIntoCutBox.IsChecked = m.RapidIntoCutClearanceMm is not null;
        RapidIntoCutClearanceBox.Text = F(m.RapidIntoCutClearanceMm ?? new MotionSettings().RapidIntoCutClearanceMm ?? m.PlungeClearanceMm / 4);
        EndPositionPicker.SelectedIndex = (int)m.EndPosition;
        EndXBox.Text = F(m.EndXMm); EndYBox.Text = F(m.EndYMm);
        UpdateMotionPanels();
        ZMaxBox.Text = m.StockTopMachineZ is double z ? F(z) : "";
        double margin = _document.Machine.LimitMarginMm;
        ZHeadroomText.Text = m.ZMaxAboveStockMm is double h
            ? $"= {F(h)} mm fritt över materialet upp till Z-brytaren (tillåtet {F(h - margin)} mm med " +
              $"{F(margin)} mm marginal). Djupast tillåtna: {F(_document.Machine.ZTravelMm - h - margin)} mm under ytan."
            : "Ej angivet – Z-brytaren kan inte kontrolleras.";
    }

    private void SaveMotion_Click(object sender, RoutedEventArgs e)
    {
        if (!Parse(TravelHeightBox, out var travel) || !Parse(PlungeClearanceBox, out var plunge) ||
            !Parse(SpinUpBox, out var spinUp))
        { Error("Ange giltiga Z-höjder."); return; }
        double margin = _document.Machine.LimitMarginMm;
        double? stockTop = null;
        if (!string.IsNullOrWhiteSpace(ZMaxBox.Text))
        {
            if (!Parse(ZMaxBox, out var z)) { Error("Ogiltig MPos Z. Skriv ett negativt tal (t.ex. -45.2), eller lämna fältet tomt om värdet är okänt.", ZMaxBox); return; }
            stockTop = z;
        }
        double? rapidInto = null;
        if (RapidIntoCutBox.IsChecked == true)
        {
            if (!Parse(RapidIntoCutClearanceBox, out var rc)) { Error("Ange höjden över fräst djup i mm.", RapidIntoCutClearanceBox); return; }
            rapidInto = rc;
        }
        var end = (EndPosition)Math.Max(0, EndPositionPicker.SelectedIndex);
        double endX = _document.Motion.EndXMm, endY = _document.Motion.EndYMm;
        if (end == EndPosition.Point && (!Parse(EndXBox, out endX) || !Parse(EndYBox, out endY)))
        { Error("Ange slutpunktens X och Y i mm.", EndXBox); return; }
        var candidate = new MotionSettings { TravelHeightMm = travel, PlungeClearanceMm = plunge,
            StockTopMachineZ = stockTop, SpindleSpinUpSeconds = spinUp,
            RapidIntoCutClearanceMm = rapidInto, EndPosition = end, EndXMm = endX, EndYMm = endY };
        if (MotionValidation.GetError(candidate) is string error) { Error(error); return; }
        RememberChange();
        _document.Motion = candidate;
        RefreshMotionFields();
        Render();
        StatusText.Text = candidate.ZMaxAboveStockMm is double h
            ? $"Z-höjder sparade. {F(h)} mm upp till Z-brytaren; rörelser kontrolleras mot {F(h - margin)} mm över materialet."
            : "Z-höjder sparade. MPos Z vid materialytan är inte angiven – Z-brytaren kan inte kontrolleras.";
    }

    private void RapidIntoCut_Click(object sender, RoutedEventArgs e) => UpdateMotionPanels();
    private void EndPosition_Changed(object sender, SelectionChangedEventArgs e) => UpdateMotionPanels();

    private void UpdateMotionPanels()
    {
        if (RapidIntoCutPanel is null || EndPointPanel is null || EndPositionPicker is null) return;
        RapidIntoCutPanel.Visibility = RapidIntoCutBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EndPointPanel.Visibility = EndPositionPicker.SelectedIndex == (int)EndPosition.Point ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Rapid XY moves as short thin dashes; moves raised over a fixture in violet.</summary>
    private void DrawRapids(Canvas canvas, double offsetX, double offsetY)
    {
        foreach (var move in _camReport.Motion.Moves)
        {
            if (move.Kind != MoveKind.Rapid || !move.MovesXY) continue;
            bool raised = move.FixtureName is not null;
            canvas.Children.Add(new Line {
                X1 = move.X0 + offsetX, Y1 = move.Y0 + offsetY, X2 = move.X1 + offsetX, Y2 = move.Y1 + offsetY,
                Stroke = raised ? CanvasTheme.RaisedRapid : CanvasTheme.Rapid,
                StrokeThickness = (raised ? 1.4 : 1.0) / _zoom,
                StrokeDashArray = new DoubleCollection { 3, 4 },
                IsHitTestVisible = false });
        }
    }

    /// <summary>
    /// Draws toolpaths as a translucent band of the real tool width with a thin centre line,
    /// direction arrows, tabs, conflict marks and failed/stale operations.
    /// </summary>
    private void DrawCamResults(Canvas canvas, double offsetX, double offsetY)
    {
        // Bands first (all operations) so every centre line, tab and mark stays on top.
        foreach (var result in _camReport.Operations)
        {
            if (result.Preview is not ContourPreview preview) continue;
            double width = Math.Max(result.Operation.ToolDiameter, 0.5 / _zoom);
            if (preview.IsDrill) { canvas.Children.Add(HoleDisc(preview.Centerline[0], result.Operation.ToolDiameter, offsetX, offsetY, CanvasTheme.ToolBand, null)); continue; }
            foreach (var centerline in preview.Centerlines)
                canvas.Children.Add(PathLine(centerline, offsetX, offsetY, CanvasTheme.ToolBand, width));
        }
        DrawRapids(canvas, offsetX, offsetY);
        foreach (var result in _camReport.Operations)
        {
            if (result.Preview is ContourPreview { IsDrill: true } hole)
            {
                // A drilled hole: its outline and a centre cross (no direction, no tabs).
                var c = hole.Centerline[0];
                canvas.Children.Add(HoleDisc(c, result.Operation.ToolDiameter, offsetX, offsetY, null, CanvasTheme.ToolPath));
                double arm = Math.Max(result.Operation.ToolDiameter * 0.7, 6 / _zoom);
                canvas.Children.Add(new Line { X1 = c.X + offsetX - arm, X2 = c.X + offsetX + arm, Y1 = c.Y + offsetY, Y2 = c.Y + offsetY, Stroke = CanvasTheme.ToolPath, StrokeThickness = 1 / _zoom, IsHitTestVisible = false });
                canvas.Children.Add(new Line { X1 = c.X + offsetX, X2 = c.X + offsetX, Y1 = c.Y + offsetY - arm, Y2 = c.Y + offsetY + arm, Stroke = CanvasTheme.ToolPath, StrokeThickness = 1 / _zoom, IsHitTestVisible = false });
                AddConflictMarks(canvas, hole, result.Findings.Where(f => f.Severity == CamSeverity.Warning), offsetX, offsetY);
                AddConflictMarks(canvas, hole, result.Findings.Where(f => f.Severity == CamSeverity.Critical), offsetX, offsetY);
            }
            else if (result.Preview is ContourPreview preview)
            {
                foreach (var centerline in preview.Centerlines)
                    canvas.Children.Add(PathLine(centerline, offsetX, offsetY, CanvasTheme.ToolPath, 1.1 / _zoom));
                if (result.Operation.Kind != CamOperationKind.Pocket)
                    foreach (var centerline in preview.Centerlines)
                        DrawDirectionArrows(canvas, centerline, offsetX, offsetY);
                DrawTabs(canvas, preview, result.TabSpans, result.Operation.ToolDiameter, offsetX, offsetY);
                // Warnings first so critical marks are drawn on top.
                AddConflictMarks(canvas, preview, result.Findings.Where(f => f.Severity == CamSeverity.Warning), offsetX, offsetY);
                AddConflictMarks(canvas, preview, result.Findings.Where(f => f.Severity == CamSeverity.Critical), offsetX, offsetY);
            }
            else if (result.Shape is DesignShape shape)
            {
                // No path: never leave the object looking as if it were fine.
                var brush = result.Stale ? CanvasTheme.Warning : CanvasTheme.Critical;
                var outline = new Rectangle { Width = shape.Width, Height = shape.Height, Stroke = brush,
                    StrokeThickness = 1.4 / _zoom, StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false };
                Canvas.SetLeft(outline, shape.X + offsetX); Canvas.SetTop(outline, shape.Y + offsetY);
                canvas.Children.Add(outline);
                var label = new Border {
                    Background = new SolidColorBrush(Color.FromArgb(225, 0x10, 0x1B, 0x27)),
                    BorderBrush = brush, BorderThickness = new Thickness(1 / _zoom),
                    CornerRadius = new CornerRadius(2 / _zoom),
                    Padding = new Thickness(4 / _zoom, 1 / _zoom, 4 / _zoom, 1 / _zoom),
                    IsHitTestVisible = false,
                    Child = new TextBlock { Text = result.Stale ? "Körbana inaktuell" : "CAM-fel – ej kontrollerad",
                        FontSize = 10.5 / _zoom, FontWeight = FontWeights.SemiBold, Foreground = brush } };
                Canvas.SetLeft(label, shape.X + offsetX + 3 / _zoom); Canvas.SetTop(label, shape.Y + offsetY + 3 / _zoom);
                canvas.Children.Add(label);
            }
        }
    }

    private Ellipse HoleDisc(CamPoint c, double diameter, double offsetX, double offsetY, Brush? fill, Brush? stroke)
    {
        double d = Math.Max(diameter, 1 / _zoom);
        var e = new Ellipse { Width = d, Height = d, Fill = fill, Stroke = stroke, StrokeThickness = 1.1 / _zoom, IsHitTestVisible = false };
        Canvas.SetLeft(e, c.X - d / 2 + offsetX); Canvas.SetTop(e, c.Y - d / 2 + offsetY);
        return e;
    }

    private static Polyline PathLine(IReadOnlyList<CamPoint> path, double offsetX, double offsetY, Brush stroke, double thickness)
    {
        var line = new Polyline { Stroke = stroke, StrokeThickness = thickness, IsHitTestVisible = false,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        foreach (var point in path) line.Points.Add(new Point(point.X + offsetX, point.Y + offsetY));
        return line;
    }

    /// <summary>Small chevrons along the path (about every 90 screen pixels) showing travel direction.</summary>
    private void DrawDirectionArrows(Canvas canvas, IReadOnlyList<CamPoint> path, double offsetX, double offsetY)
    {
        if (path.Count < 2) return;
        double spacing = 90 / _zoom, size = 5 / _zoom, next = spacing / 2, walked = 0;
        int drawn = 0;
        for (int i = 1; i < path.Count && drawn < 40; i++)
        {
            var a = path[i - 1]; var b = path[i];
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) continue;
            double ux = dx / len, uy = dy / len;
            while (next <= walked + len && drawn < 40)
            {
                double t = (next - walked) / len;
                double x = a.X + dx * t + offsetX, y = a.Y + dy * t + offsetY;
                var chevron = new Polyline { Stroke = CanvasTheme.Arrow, StrokeThickness = 1.3 / _zoom,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
                chevron.Points.Add(new Point(x - ux * size - uy * size * 0.8, y - uy * size + ux * size * 0.8));
                chevron.Points.Add(new Point(x, y));
                chevron.Points.Add(new Point(x - ux * size + uy * size * 0.8, y - uy * size - ux * size * 0.8));
                canvas.Children.Add(chevron);
                drawn++;
                next += spacing;
            }
            walked += len;
        }
    }

    /// <summary>
    /// Tabs as short amber bridges across the tool band (with a thin dark outline), at least a
    /// few pixels wide so they stay visible at any zoom. The tool lifts over these.
    /// </summary>
    private void DrawTabs(Canvas canvas, ContourPreview preview, IReadOnlyList<TabSpan> tabs,
        double toolDiameter, double offsetX, double offsetY)
    {
        var paths = preview.Centerlines.Count > 0 ? preview.Centerlines :
            new IReadOnlyList<CamPoint>[] { preview.Centerline };
        double width = Math.Max(toolDiameter, 7 / _zoom);
        foreach (var tab in tabs)
        {
            if (tab.PathIndex >= paths.Count) continue;
            var points = new PointCollection(PathMath.Slice(paths[tab.PathIndex], tab.Start, tab.End)
                .Select(p => new Point(p.X + offsetX, p.Y + offsetY)));
            canvas.Children.Add(new Polyline { Points = points, Stroke = CanvasTheme.TabEdge,
                StrokeThickness = width + 2 / _zoom, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
                IsHitTestVisible = false });
            canvas.Children.Add(new Polyline { Points = points, Stroke = CanvasTheme.Tab,
                StrokeThickness = width, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
                IsHitTestVisible = false });
        }
    }

    /// <summary>
    /// Draws only the affected part (TStart..TEnd) of each segment-level finding, as a soft
    /// glow with a sharp core. offsetX/Y converts stock-local path coordinates to the target canvas.
    /// </summary>
    private void AddConflictMarks(Canvas canvas, ContourPreview preview,
        IEnumerable<CamFinding> findings, double offsetX, double offsetY)
    {
        var paths = preview.Centerlines.Count > 0 ? preview.Centerlines :
            new IReadOnlyList<CamPoint>[] { preview.Centerline };
        foreach (var f in findings)
        {
            if (!f.HasSegment || f.PathIndex >= paths.Count) continue;
            var path = paths[f.PathIndex];
            if (f.SegmentIndex + 1 >= path.Count) continue;
            var a = path[f.SegmentIndex]; var b = path[f.SegmentIndex + 1];
            double t0 = Math.Clamp(f.TStart, 0, 1), t1 = Math.Clamp(f.TEnd, 0, 1);
            bool critical = f.Severity == CamSeverity.Critical;
            double x1 = a.X + (b.X - a.X) * t0 + offsetX, y1 = a.Y + (b.Y - a.Y) * t0 + offsetY;
            double x2 = a.X + (b.X - a.X) * t1 + offsetX, y2 = a.Y + (b.Y - a.Y) * t1 + offsetY;
            var layers = new[] {
                (critical ? CanvasTheme.CriticalGlow : CanvasTheme.WarningGlow, 7 / _zoom),
                (critical ? CanvasTheme.Critical : CanvasTheme.Warning, 1.8 / _zoom) };
            foreach (var (brush, thickness) in layers)
                canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                    Stroke = brush, StrokeThickness = thickness,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false });
        }
    }

    /// <summary>Live summary in the CAM panel. Never states that anything is approved.</summary>
    private void UpdateCamHealth()
    {
        if (CamHealthHeader is null || CamHealthText is null) return;
        UpdateCamTree();
        var r = _camReport;
        int stale = r.StaleCount;
        if (ComputePathsButton is not null)
        {
            ComputePathsButton.Content = stale > 0 ? $"Beräkna körbanor ({stale} inaktuella)" : "Beräkna körbanor";
            ComputePathsButton.Background = stale > 0
                ? new SolidColorBrush(Color.FromRgb(196, 110, 20))
                : new SolidColorBrush(Color.FromRgb(35, 98, 63));
        }
        CamHealthText.Inlines.Clear();
        if (r.Operations.Count == 0)
        {
            CamHealthHeader.Text = "CAM-kontroll";
            CamHealthHeader.Foreground = CamHealthText.Foreground;
            CamHealthText.Inlines.Add(new Run("Inga CAM-operationer."));
            return;
        }
        CamHealthHeader.Text = $"CAM-kontroll: {r.CriticalCount} kritiska · {r.WarningCount} varningar · {r.InfoCount} info";
        CamHealthHeader.Foreground = r.CriticalCount > 0 ? HealthCritical : r.WarningCount > 0 ? HealthWarning : HealthOk;

        // Group per object and code so one clamp over a long edge is one line, not fifty.
        var lines = r.Operations
            .OrderByDescending(o => _selected is not null && o.Operation.ShapeId == _selected.Id)
            .SelectMany(o => o.Findings
                .GroupBy(f => (f.Severity, f.Code, f.FixtureId))
                .OrderBy(g => g.Key.Severity)
                .Select(g => (Severity: g.Key.Severity,
                              Text: $"{ShapeLabel(o.Shape, o.Operation.ShapeId)}: {g.First().Message}" +
                                    (g.Count() > 1 && g.First().HasSegment ? $" ({g.Count()} delsträckor)" : ""))))
            .ToList();
        // Program-wide findings (Z limit, moves over fixtures): problems first, info last.
        var global = r.GlobalFindings.Select(f => (Severity: f.Severity, Text: "Förflyttningar: " + f.Message)).ToList();
        lines.InsertRange(0, global.Where(g => g.Severity != CamSeverity.Info));
        lines.AddRange(global.Where(g => g.Severity == CamSeverity.Info));
        if (lines.Count == 0)
            CamHealthText.Inlines.Add(new Run("Inga problem hittades i de kontroller som finns."));
        foreach (var (severity, text) in lines.Take(8))
        {
            // A coloured word instead of a symbol: always renders, and is readable at a glance.
            CamHealthText.Inlines.Add(new Run(SeverityName(severity) + "  ")
                { FontWeight = FontWeights.Bold, Foreground = SeverityTextBrush(severity) });
            CamHealthText.Inlines.Add(new Run(text));
            CamHealthText.Inlines.Add(new LineBreak());
        }
        if (lines.Count > 8)
        {
            CamHealthText.Inlines.Add(new Run($"… och {lines.Count - 8} till (se Kontrollera hinder)."));
            CamHealthText.Inlines.Add(new LineBreak());
        }
        CamHealthText.Inlines.Add(new LineBreak());
        if (RunTimeText() is { Length: > 0 } time)
        {
            CamHealthText.Inlines.Add(new Run(time) { Foreground = new SolidColorBrush(Color.FromRgb(200, 220, 240)) });
            CamHealthText.Inlines.Add(new LineBreak());
            CamHealthText.Inlines.Add(new LineBreak());
        }
        CamHealthText.Inlines.Add(new Run("Kontrolleras: inställningar, djup, materialgränser, hållflikar, XY mot " +
            "fastspänningar, maskinens gränser, andra delar, Z-höjder och snabbförflyttningar. " +
            "Ej kontrollerat: hållare/spännhylsa, matningshastigheter, nedre Z-gräns. Detta är inget säkerhetsgodkännande.")
            { Foreground = new SolidColorBrush(Color.FromRgb(160, 178, 196)) });
    }

    private static Brush SeverityTextBrush(CamSeverity s) => s switch
    {
        CamSeverity.Critical => HealthCritical,
        CamSeverity.Warning => HealthWarning,
        _ => new SolidColorBrush(Color.FromRgb(130, 190, 255))
    };

    private string ShapeLabel(DesignShape? shape, Guid id)
    {
        if (shape is null) return $"Okänt objekt {id.ToString()[..4]}";
        return CamPlanner.Label(_document, shape);
    }

    /// <summary>"Beräkna körbanor": recomputes every outdated toolpath.</summary>
    private void PreviewContour_Click(object sender, RoutedEventArgs e)
    {
        if (_document.CamOperations.Count == 0)
        { Error("Det finns inga sparade CAM-operationer. Spara CAM för ett objekt först."); return; }
        int staleBefore = _camReport.StaleCount;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try { _camReport = _camPlanner.Review(_document, computeAll: true); }
        finally { Mouse.OverrideCursor = null; }
        _showContourPreview = true;
        if (ContourVisibilityCheck is not null) ContourVisibilityCheck.IsChecked = true;
        Render();
        StatusText.Text = $"Körbanor beräknade ({staleBefore} var inaktuella). " +
            $"{_camReport.CriticalCount} kritiska, {_camReport.WarningCount} varningar. Ej körbar G-code.";
    }

    /// <summary>Full report dialog. Computes outdated paths first so everything is checked.</summary>
    private void ReviewCamSafety_Click(object sender, RoutedEventArgs e)
    {
        if (MachineSetupValidation.ValidateProfile(_document.Machine) is string setupError)
        { Error(setupError); return; }
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try { _camReport = _camPlanner.Review(_document, computeAll: true); }
        finally { Mouse.OverrideCursor = null; }
        var r = _camReport;
        var lines = new List<string>();
        foreach (var o in r.Operations)
            foreach (var g in o.Findings.GroupBy(f => (f.Severity, f.Code, f.FixtureId)).OrderBy(g => g.Key.Severity))
                lines.Add($"{SeverityName(g.Key.Severity)} – {ShapeLabel(o.Shape, o.Operation.ShapeId)}: {g.First().Message}" +
                          (g.First().HasSegment ? $" ({g.Count()} delsträckor)" : ""));
        foreach (var f in r.GlobalFindings.OrderBy(f => f.Severity))
            lines.Add($"{SeverityName(f.Severity)} – Förflyttningar: {f.Message}");
        if (r.Operations.Count == 0) lines.Add("Inga CAM-operationer är sparade.");
        else if (lines.Count == 0) lines.Add("Inga problem hittades i de kontroller som finns.");
        var header = $"{r.Operations.Count} operationer · {r.CriticalCount} kritiska · {r.WarningCount} varningar · " +
                     $"{r.InfoCount} info · {r.NotAnalyzedCount} ej kontrollerade.\n\n";
        var limitations = "\n\nKONTROLLERAS: CAM-inställningar, djup och kvarvarande botten, materialgränser, " +
            "hållflikar, skärande XY-banor mot fastspänningar, maskinens XY-gränser, andra delar med CAM, Z-höjder mot fastspänningar och Z-brytaren samt att snabbförflyttningar aldrig går i materialet.\n" +
            "KONTROLLERAS INTE: hållare/spännhylsa, matningshastigheter, nedre Z-gräns, " +
            "materialets verkliga läge och maskinens verkliga inställningar. Detta är INTE ett säkerhetsgodkännande.";
        _showContourPreview = true;
        Render();
        StatusText.Text = $"CAM-kontroll: {r.CriticalCount} kritiska, {r.WarningCount} varningar, {r.NotAnalyzedCount} ej kontrollerade.";
        AppDialog.Show(this, header + string.Join("\n", lines.Take(30)) +
            (lines.Count > 30 ? $"\n...och {lines.Count - 30} fler." : "") + limitations,
            $"{AppTitle} – CAM-kontroll", MessageBoxButton.OK,
            r.CriticalCount > 0 ? MessageBoxImage.Error :
            r.WarningCount > 0 || r.NotAnalyzedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static string SeverityName(CamSeverity s) => s switch
    {
        CamSeverity.Critical => "KRITISKT", CamSeverity.Warning => "Varning", _ => "Info"
    };
}
