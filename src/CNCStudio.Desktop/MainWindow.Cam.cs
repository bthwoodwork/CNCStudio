using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    private const string AppTitle = "CNC Studio 0.5.7";

    private readonly CamPlanner _camPlanner = new(VectorContourPreview.Generate);
    private CamReport _camReport = CamReport.Empty;

    private static readonly Brush CriticalBrush = Brushes.Red;
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(255, 140, 0));
    private static readonly Brush HealthCritical = new SolidColorBrush(Color.FromRgb(255, 120, 110));
    private static readonly Brush HealthWarning = new SolidColorBrush(Color.FromRgb(255, 196, 110));
    private static readonly Brush HealthOk = new SolidColorBrush(Color.FromRgb(150, 220, 160));

    /// <summary>Cheap shapes are recomputed if changed; heavy shapes are marked stale.</summary>
    private void RefreshCamReport() => _camReport = _camPlanner.Review(_document);

    /// <summary>Draws toolpaths, conflict marks and failed/stale operations on a canvas.</summary>
    private void DrawCamResults(Canvas canvas, double offsetX, double offsetY)
    {
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
        if (r.Operations.Count == 0)
        {
            CamHealthHeader.Text = "CAM-kontroll";
            CamHealthHeader.Foreground = CamHealthText.Foreground;
            CamHealthText.Text = "Inga CAM-operationer.";
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
                .Select(g => $"{Symbol(g.Key.Severity)} {ShapeLabel(o.Shape, o.Operation.ShapeId)}: {g.First().Message}" +
                             (g.Count() > 1 && g.First().HasSegment ? $" ({g.Count()} delsträckor)" : "")))
            .ToList();
        var text = lines.Count == 0
            ? "Inga problem hittades i de kontroller som finns."
            : string.Join("\n", lines.Take(8)) + (lines.Count > 8 ? $"\n… och {lines.Count - 8} till (se Kontrollera hinder)." : "");
        CamHealthText.Text = text + "\n\nKontrolleras: inställningar, XY mot fastspänningar och maskinens gränser. " +
            "Ej kontrollerat: Z-rörelser, hållare, materialgränser. Detta är inget säkerhetsgodkännande.";
    }

    private static string Symbol(CamSeverity s) => s switch
    {
        CamSeverity.Critical => "⛔", CamSeverity.Warning => "⚠", _ => "ℹ"
    };

    private string ShapeLabel(DesignShape? shape, Guid id)
    {
        if (shape is null) return $"Okänt objekt {id.ToString()[..4]}";
        int index = _document.Shapes.IndexOf(shape) + 1;
        string kind = shape.Kind switch
        {
            ShapeKind.Rectangle => "Rektangel", ShapeKind.Ellipse => "Ellips", _ => "Vektorform"
        };
        return $"{kind} {index}";
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
        if (r.Operations.Count == 0) lines.Add("Inga CAM-operationer är sparade.");
        else if (lines.Count == 0) lines.Add("Inga problem hittades i de kontroller som finns.");
        var header = $"{r.Operations.Count} operationer · {r.CriticalCount} kritiska · {r.WarningCount} varningar · " +
                     $"{r.InfoCount} info · {r.NotAnalyzedCount} ej kontrollerade.\n\n";
        var limitations = "\n\nKONTROLLERAS: CAM-inställningar, skärande XY-banor mot fastspänningar och maskinens XY-gränser.\n" +
            "KONTROLLERAS INTE: Z-rörelser, snabbförflyttningar, hållare/spännhylsa, materialgränser, " +
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
