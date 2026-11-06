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
    private const string AppTitle = "CNC Studio 0.7.1";

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
            if (!Parse(ZMaxBox, out var z)) { Error("Ogiltig MPos Z. Lämna fältet tomt om värdet är okänt."); return; }
            stockTop = z;
        }
        var candidate = new MotionSettings { TravelHeightMm = travel, PlungeClearanceMm = plunge,
            StockTopMachineZ = stockTop, SpindleSpinUpSeconds = spinUp };
        if (MotionValidation.GetError(candidate) is string error) { Error(error); return; }
        RememberChange();
        _document.Motion = candidate;
        RefreshMotionFields();
        Render();
        StatusText.Text = candidate.ZMaxAboveStockMm is double h
            ? $"Z-höjder sparade. {F(h)} mm upp till Z-brytaren; rörelser kontrolleras mot {F(h - margin)} mm över materialet."
            : "Z-höjder sparade. MPos Z vid materialytan är inte angiven – Z-brytaren kan inte kontrolleras.";
    }

    private static readonly Brush RapidBrush = new SolidColorBrush(Color.FromRgb(70, 170, 255));
    private static readonly Brush RaisedRapidBrush = new SolidColorBrush(Color.FromRgb(214, 70, 214));

    /// <summary>Rapid XY moves as thin dashed lines; moves raised over a fixture in magenta.</summary>
    private void DrawRapids(Canvas canvas, double offsetX, double offsetY)
    {
        foreach (var move in _camReport.Motion.Moves)
        {
            if (move.Kind != MoveKind.Rapid || !move.MovesXY) continue;
            bool raised = move.FixtureName is not null;
            canvas.Children.Add(new Line {
                X1 = move.X0 + offsetX, Y1 = move.Y0 + offsetY, X2 = move.X1 + offsetX, Y2 = move.Y1 + offsetY,
                Stroke = raised ? RaisedRapidBrush : RapidBrush,
                StrokeThickness = (raised ? 2.2 : 1.4) / _zoom,
                StrokeDashArray = new DoubleCollection { 2, 3 },
                IsHitTestVisible = false });
        }
    }

    /// <summary>Draws toolpaths, conflict marks and failed/stale operations on a canvas.</summary>
    private void DrawCamResults(Canvas canvas, double offsetX, double offsetY)
    {
        DrawRapids(canvas, offsetX, offsetY);
        foreach (var result in _camReport.Operations)
        {
            if (result.Preview is ContourPreview preview)
            {
                foreach (var centerline in preview.Centerlines)
                {
                    var line = new Polyline { Stroke = Brushes.LimeGreen, StrokeThickness = 2.4 / _zoom,
                        IsHitTestVisible = false, StrokeDashArray = new DoubleCollection { 5, 2 } };
                    foreach (var point in centerline) line.Points.Add(new Point(point.X + offsetX, point.Y + offsetY));
                    canvas.Children.Add(line);
                }
                DrawTabs(canvas, preview, result.TabSpans, offsetX, offsetY);
                // Warnings first so critical marks are drawn on top.
                AddConflictMarks(canvas, preview, result.Findings.Where(f => f.Severity == CamSeverity.Warning), offsetX, offsetY);
                AddConflictMarks(canvas, preview, result.Findings.Where(f => f.Severity == CamSeverity.Critical), offsetX, offsetY);
            }
            else if (result.Shape is DesignShape shape)
            {
                // No path: never leave the object looking as if it were fine.
                var brush = result.Stale ? WarningBrush : CriticalBrush;
                var outline = new Rectangle { Width = shape.Width, Height = shape.Height, Stroke = brush,
                    StrokeThickness = 2.5 / _zoom, StrokeDashArray = new DoubleCollection { 3, 2 },
                    IsHitTestVisible = false };
                Canvas.SetLeft(outline, shape.X + offsetX); Canvas.SetTop(outline, shape.Y + offsetY);
                canvas.Children.Add(outline);
                var label = new TextBlock { Text = result.Stale ? "Körbana inaktuell" : "CAM-fel – ej kontrollerad",
                    FontSize = 11 / _zoom, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                    Background = brush, Padding = new Thickness(3 / _zoom, 1 / _zoom, 3 / _zoom, 1 / _zoom),
                    IsHitTestVisible = false };
                Canvas.SetLeft(label, shape.X + offsetX); Canvas.SetTop(label, shape.Y + offsetY);
                canvas.Children.Add(label);
            }
        }
    }

    private static readonly Brush TabBrush = new SolidColorBrush(Color.FromRgb(255, 214, 10));
    private static readonly Brush TabEdgeBrush = new SolidColorBrush(Color.FromRgb(90, 62, 0));

    /// <summary>
    /// Tabs as thick yellow bars with a dark edge, sized in screen pixels so they stay
    /// visible at any zoom and on top of the selection outline. The tool lifts over these.
    /// </summary>
    private void DrawTabs(Canvas canvas, ContourPreview preview, IReadOnlyList<TabSpan> tabs,
        double offsetX, double offsetY)
    {
        var paths = preview.Centerlines.Count > 0 ? preview.Centerlines :
            new IReadOnlyList<CamPoint>[] { preview.Centerline };
        foreach (var tab in tabs)
        {
            if (tab.PathIndex >= paths.Count) continue;
            var points = new PointCollection(PathMath.Slice(paths[tab.PathIndex], tab.Start, tab.End)
                .Select(p => new Point(p.X + offsetX, p.Y + offsetY)));
            canvas.Children.Add(new Polyline { Points = points, Stroke = TabEdgeBrush,
                StrokeThickness = 11 / _zoom, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square,
                IsHitTestVisible = false });
            canvas.Children.Add(new Polyline { Points = points, Stroke = TabBrush,
                StrokeThickness = 8 / _zoom, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
                IsHitTestVisible = false });
        }
    }

    /// <summary>
    /// Draws only the affected part (TStart..TEnd) of each segment-level finding.
    /// offsetX/Y converts stock-local path coordinates to the target canvas.
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
            canvas.Children.Add(new Line {
                X1 = a.X + (b.X - a.X) * t0 + offsetX, Y1 = a.Y + (b.Y - a.Y) * t0 + offsetY,
                X2 = a.X + (b.X - a.X) * t1 + offsetX, Y2 = a.Y + (b.Y - a.Y) * t1 + offsetY,
                Stroke = f.Severity == CamSeverity.Critical ? CriticalBrush : WarningBrush,
                StrokeThickness = 3.5 / _zoom,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false });
        }
    }

    /// <summary>Live summary in the CAM panel. Never states that anything is approved.</summary>
    private void UpdateCamHealth()
    {
        if (CamHealthHeader is null || CamHealthText is null) return;
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
        if (MachineSetupValidation.Validate(_document) is string setupError)
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
        MessageBox.Show(this, header + string.Join("\n", lines.Take(30)) +
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
