using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Flytta hållflikarna själv: in the design view the yellow tabs can be dragged along the toolpath; right-click a tab
/// to remove it or spread the tabs evenly again, right-click the path to add one. Once moved, the operation's tabs are
/// hand-placed (<see cref="CamOperation.ManualTabs"/>): each lands on the path point nearest to where it was put.
/// </summary>
public partial class MainWindow
{
    /// <summary>A tab in the drawing: which shape's operation and which of its tabs.</summary>
    private sealed record TabHandle(Guid ShapeId, int Index);

    private TabHandle? _tabDrag;
    private Ellipse? _tabGhost;
    private Point _tabDropAt;

    private (CamOperation Op, CamOperationResult Result, DesignShape Shape)? TabTarget(Guid shapeId)
    {
        var op = _document.CamOperations.FirstOrDefault(o => o.ShapeId == shapeId);
        var result = _camReport.Operations.FirstOrDefault(r => r.Operation.ShapeId == shapeId && r.Operation.Parent is null);
        var shape = _document.Shapes.FirstOrDefault(s => s.Id == shapeId);
        return op is null || result?.Preview is null || shape is null ? null : (op, result, shape);
    }

    private static IReadOnlyList<IReadOnlyList<CamPoint>> PathsOf(ContourPreview p) =>
        p.Centerlines.Count > 0 ? p.Centerlines : new IReadOnlyList<CamPoint>[] { p.Centerline };

    private bool BeginTabDrag(object? tag, Point p)
    {
        if (tag is not TabHandle h || _machineView) return false;
        _tabDrag = h; _tabDropAt = p;
        double r = 5 / _zoom;
        _tabGhost = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = CanvasTheme.Tab, Stroke = CanvasTheme.TabEdge, StrokeThickness = 1.5 / _zoom, IsHitTestVisible = false };
        Canvas.SetLeft(_tabGhost, p.X - r); Canvas.SetTop(_tabGhost, p.Y - r);
        DesignCanvas.Children.Add(_tabGhost);
        DesignCanvas.CaptureMouse();
        StatusText.Text = "Dra fliken längs banan och släpp där den ska sitta.";
        return true;
    }

    private bool MoveTabDrag(Point p)
    {
        if (_tabDrag is not { } h || _tabGhost is null) return false;
        if (TabTarget(h.ShapeId) is not { } t) return true;
        var paths = PathsOf(t.Result.Preview!);
        var (pi, s, _) = CNCStudio.Core.CamTabs.Nearest(paths, p.X, p.Y);
        if (pi < 0) return true;
        var q = PathMath.PointAt(paths[pi], s);
        _tabDropAt = new Point(q.X, q.Y);
        double r = _tabGhost.Width / 2;
        Canvas.SetLeft(_tabGhost, q.X - r); Canvas.SetTop(_tabGhost, q.Y - r);
        return true;
    }

    private bool EndTabDrag()
    {
        if (_tabDrag is not { } h) return false;
        _tabDrag = null;
        if (_tabGhost is not null) { DesignCanvas.Children.Remove(_tabGhost); _tabGhost = null; }
        DesignCanvas.ReleaseMouseCapture();
        if (TabTarget(h.ShapeId) is not { } t) return true;
        var points = CNCStudio.Core.CamTabs.Centres(t.Result.Preview!, t.Result.TabSpans, t.Shape);
        if (h.Index < 0 || h.Index >= points.Count) return true;
        points[h.Index] = new TabPoint(Math.Round(_tabDropAt.X - t.Shape.X, 3), Math.Round(_tabDropAt.Y - t.Shape.Y, 3));
        SetManualTabs(t.Op, points, "Fliken är flyttad.");
        return true;
    }

    private void SetManualTabs(CamOperation op, List<TabPoint>? points, string message)
    {
        RememberChange();
        op.ManualTabs = points;
        _camReport = _camPlanner.Review(_document, computeShapes: new[] { op.ShapeId });
        Render();
        var placed = _camReport.Operations.FirstOrDefault(r => r.Operation.ShapeId == op.ShapeId && r.Operation.Parent is null)?.TabSpans.Count ?? 0;
        StatusText.Text = message + (points is { Count: > 0 } && placed < points.Count ? $" {points.Count - placed} flik(ar) fick inte plats (för nära en annan)." : "");
    }

    /// <summary>Right-click: on a tab – remove it or spread the tabs evenly; on a cut path with tabs – add one there.</summary>
    private bool TabContextMenu(object? tag, Point p)
    {
        if (_machineView || _tool != Tool.Select) return false;
        var menu = new ContextMenu();
        if (tag is TabHandle h && TabTarget(h.ShapeId) is { } t)
        {
            var remove = new MenuItem { Header = "Ta bort fliken" };
            remove.Click += (_, _) =>
            {
                var points = CNCStudio.Core.CamTabs.Centres(t.Result.Preview!, t.Result.TabSpans, t.Shape);
                if (h.Index < points.Count) points.RemoveAt(h.Index);
                if (points.Count == 0) { SetManualTabs(t.Op, null, ""); t.Op.UseTabs = false; Render(); StatusText.Text = "Sista fliken borttagen – hållflikarna är avstängda."; return; }
                SetManualTabs(t.Op, points, "Fliken är borttagen.");
            };
            menu.Items.Add(remove);
            var even = new MenuItem { Header = "Fördela flikarna jämnt igen", IsEnabled = t.Op.ManualTabs is { Count: > 0 } };
            even.Click += (_, _) => SetManualTabs(t.Op, null, $"Flikarna fördelas jämnt igen ({t.Op.TabCount} st).");
            menu.Items.Add(even);
        }
        else
        {
            // Near the cut path of a shape whose cut has tabs: add one.
            CamOperationResult? hit = null; double best = 8 / _zoom;
            foreach (var r in _camReport.Operations.Where(r => r.Operation.Parent is null && r.Preview is not null && r.TabSpans.Count > 0))
            {
                var (pi, _, d) = CNCStudio.Core.CamTabs.Nearest(PathsOf(r.Preview!), p.X, p.Y);
                if (pi >= 0 && d < best) { best = d; hit = r; }
            }
            if (hit is null || TabTarget(hit.Operation.ShapeId) is not { } t2) return false;
            var add = new MenuItem { Header = "Lägg till flik här" };
            add.Click += (_, _) =>
            {
                var points = CNCStudio.Core.CamTabs.Centres(t2.Result.Preview!, t2.Result.TabSpans, t2.Shape);
                var paths = PathsOf(t2.Result.Preview!);
                var (pi, s, _) = CNCStudio.Core.CamTabs.Nearest(paths, p.X, p.Y);
                var q = PathMath.PointAt(paths[pi], s);
                points.Add(new TabPoint(Math.Round(q.X - t2.Shape.X, 3), Math.Round(q.Y - t2.Shape.Y, 3)));
                SetManualTabs(t2.Op, points, "Fliken är tillagd.");
            };
            menu.Items.Add(add);
        }
        menu.PlacementTarget = DesignCanvas;
        menu.IsOpen = true;
        return true;
    }
}
