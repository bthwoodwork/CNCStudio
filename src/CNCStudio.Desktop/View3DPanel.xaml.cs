using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CNCStudio.Core;
using HelixToolkit.Wpf;

namespace CNCStudio.Desktop;

/// <summary>
/// 3D view in work coordinates (X right, Y towards the back, Z up; origin at the material's
/// front-left top corner): simulated material and toolpaths.
/// The material simulation runs in the background and only when the CAM report changes.
/// </summary>
public partial class View3DPanel : UserControl
{
    /// <summary>Vertices in the displayed surface; the simulation grid is thinned to this.</summary>
    private const int MaxDisplayVertices = 250_000;

    private readonly ModelVisual3D _stock = new();
    private readonly LinesVisual3D _cuts = new() { Color = Color.FromRgb(46, 155, 255), Thickness = 1.4 };
    private readonly LinesVisual3D _plunges = new() { Color = Color.FromRgb(255, 176, 0), Thickness = 1.4 };
    private readonly LinesVisual3D _rapids = new() { Color = Color.FromRgb(255, 77, 77), Thickness = 1.0 };
    private readonly ModelVisual3D _fixtures = new();
    private readonly ModelVisual3D _zero = new();
    private readonly ModelVisual3D _table = new();
    private readonly ModelVisual3D _tableRuler = new();
    private readonly ModelVisual3D _stockRuler = new();
    private readonly ModelVisual3D _tool = new();
    private bool _toolShown;

    /// <summary>The tool in the view's frame; <paramref name="Z"/> = the tip's work Z (null = unknown).</summary>
    public readonly record struct ToolPosition(double X, double Y, double? Z);
    // The running program: what has run (orange), what is left (blue) and where the tool is.
    private readonly LinesVisual3D _jobDone = new() { Color = Color.FromRgb(255, 159, 58), Thickness = 2.0 };
    private readonly LinesVisual3D _jobTodo = new() { Color = Color.FromRgb(79, 179, 255), Thickness = 1.4 };
    private readonly LinesVisual3D _jobTool = new() { Color = Color.FromRgb(255, 77, 77), Thickness = 4 };
    private bool _jobActive;
    private CancellationTokenSource? _simulation;
    private bool _fitted;

    // ---- Dragging the material on the table (left mouse button; right rotates, middle pans) ----

    /// <summary>The material was dragged to a new place on the table: the new <see cref="CncDocument.Stock"/> X/Y.</summary>
    public event Action<double, double>? StockMoved;
    /// <summary>A left click: true on the material (select it), false elsewhere (deselect it).</summary>
    public event Action<bool>? StockClicked;
    /// <summary>Whether the material may be selected and dragged here (Design only, not in the Maskin workspace).</summary>
    public bool CanMoveStock { get; set; } = true;
    private bool _stockSelected, _stockLocked;
    private StockAnchor _anchor;
    private readonly ModelVisual3D _selectionMark = new();

    /// <summary>The material selected (in 2D or here): a frame round its top and the point X/Y is given for.</summary>
    public void SetStockSelection(bool selected, StockAnchor anchor, bool locked)
    {
        _stockSelected = selected; _anchor = anchor; _stockLocked = locked;
        BuildSelectionMark();
    }

    private void BuildSelectionMark()
    {
        _selectionMark.Children.Clear();
        if (_stockSelected && _stockW > 0)
        {
            double w = _stockW, l = _stockL, z = 0.4;
            var c = Color.FromRgb(0x3D, 0xD6, 0xF5);
            _selectionMark.Children.Add(new LinesVisual3D { Color = c, Thickness = 2.5, Points = new Point3DCollection
            {
                new(0, 0, z), new(w, 0, z), new(w, 0, z), new(w, l, z), new(w, l, z), new(0, l, z), new(0, l, z), new(0, 0, z)
            } });
            var (ax, ay) = _anchor.OnMaterial(w, l);
            _selectionMark.Children.Add(new SphereVisual3D { Center = new Point3D(ax, l - ay, z + 1), Radius = Math.Max(2, Math.Min(w, l) / 40), Fill = new SolidColorBrush(c) });
        }
        SetVisible(_selectionMark, _stockSelected && _stockW > 0);
    }
    private CncDocument? _doc;
    private Point3D? _dragFrom;
    private Vector3D _dragDelta;

    /// <summary>Where the pointer meets the material's top plane (z = 0), in the view's frame.</summary>
    private Point3D? TopPlanePoint(Point screen)
    {
        var ray = Viewport3DHelper.GetRay(Scene.Viewport, screen);
        return ray?.PlaneIntersection(new Point3D(0, 0, 0), new Vector3D(0, 0, 1));
    }

    private bool OverMaterial(Point screen) => _doc is { HasMaterial: true } d && _stockW > 0 &&
        TopPlanePoint(screen) is Point3D p && p.X >= 0 && p.X <= d.MaterialWidth && p.Y >= 0 && p.Y <= d.MaterialHeight;

    // The scene is drawn in the material's frame, so moving the material really moves the table the other way.
    // During the drag the table (and what sits on it) is shifted by −delta and the camera by −delta too: on screen the
    // table stays still and the material follows the pointer – and that is exactly the picture the rebuilt scene
    // gives when the button is released, so nothing jumps.
    private Point3D _dragCamera;

    private Visual3D[] TableFixed => new Visual3D[] { _table, _tableRuler, _fixtures, _tool };

    private void Scene_LeftDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!CanMoveStock) return;
        var pos = e.GetPosition(Scene.Viewport);
        bool over = OverMaterial(pos);
        if (!over || !_stockSelected)
        {
            // First click selects the material (or deselects it outside); dragging needs it selected.
            StockClicked?.Invoke(over);
            if (over) e.Handled = true;
            return;
        }
        if (_stockLocked) { InfoText.Text = "Materialet är låst – lås upp det under Material på bordet för att flytta det."; e.Handled = true; return; }
        if (Scene.Camera is not ProjectionCamera camera || TopPlanePoint(pos) is not Point3D start) return;
        camera.BeginAnimation(ProjectionCamera.PositionProperty, null);      // no zoom animation left running
        _dragFrom = start; _dragDelta = new Vector3D(); _dragCamera = camera.Position;
        Scene.CaptureMouse(); e.Handled = true;
    }

    private void Scene_Move(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pos = e.GetPosition(Scene.Viewport);
        if (_dragFrom is not Point3D from || _doc is not { } d)
        {
            Scene.Cursor = CanMoveStock && OverMaterial(pos) ? (_stockSelected ? (_stockLocked ? System.Windows.Input.Cursors.No : System.Windows.Input.Cursors.SizeAll) : System.Windows.Input.Cursors.Hand) : null;
            return;
        }
        e.Handled = true;
        if (Scene.Camera is not ProjectionCamera camera || TopPlanePoint(pos) is not Point3D seen) return;
        // The camera has been moved by −delta: the same screen point seen from where it started is +delta further.
        var now = seen + _dragDelta;
        // Keep the material on the work area: Stock.X grows with view X, Stock.Y (towards the front) with −view Y.
        double maxX = Math.Max(0, d.Machine.WorkWidthMm - d.MaterialWidth), maxY = Math.Max(0, d.Machine.WorkLengthMm - d.MaterialHeight);
        double dx = Math.Clamp(now.X - from.X, -d.Stock.X, Math.Max(-d.Stock.X, maxX - d.Stock.X));
        double dy = Math.Clamp(now.Y - from.Y, Math.Min(d.Stock.Y, -(maxY - d.Stock.Y)), d.Stock.Y);
        _dragDelta = new Vector3D(dx, dy, 0);
        var shift = new TranslateTransform3D(-_dragDelta);
        foreach (var v in TableFixed) v.Transform = shift;
        camera.Position = _dragCamera - _dragDelta;
        InfoText.Text = $"Material på bordet: X {F(d.Stock.X + dx)} · Y {F(d.Stock.Y - dy)} mm";
    }

    private void Scene_LeftUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragFrom is null) return;
        _dragFrom = null;
        Scene.ReleaseMouseCapture(); e.Handled = true;
        foreach (var v in TableFixed) v.Transform = Transform3D.Identity;
        if (_doc is not { } d || _dragDelta.Length < 0.001)
        {
            if (Scene.Camera is ProjectionCamera camera) camera.Position = _dragCamera;
            return;
        }
        StockMoved?.Invoke(d.Stock.X + _dragDelta.X, d.Stock.Y - _dragDelta.Y);   // rebuilds the table at −delta
    }

    public View3DPanel()
    {
        InitializeComponent();
        Scene.PreviewMouseLeftButtonDown += Scene_LeftDown;
        Scene.PreviewMouseMove += Scene_Move;
        Scene.PreviewMouseLeftButtonUp += Scene_LeftUp;
        // The checkboxes may already have added layers while the XAML loaded; SetVisible never adds twice.
        SetVisible(_zero, true);
        Layer_Changed(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Rebuilds the scene only when what it shows changed. The CAM report object is recreated on every
    /// redraw, so compare a fingerprint of its content instead of the reference.
    /// </summary>
    public void Show(CncDocument doc, CamReport report)
    {
        _doc = doc;
        var key = SceneKey(doc, report);
        if (key == _shownKey && _fitted) return;
        _shownKey = key;
        double w = doc.MaterialWidth, l = doc.MaterialHeight, t = doc.MaterialThickness;
        _stockW = w; _stockL = l;
        BuildPaths(doc, report);
        BuildFixtures(doc);
        BuildZero(doc);
        BuildTable(doc);
        BuildStockRuler(doc);
        BuildSelectionMark();
        Layer_Changed(this, new RoutedEventArgs());
        if (!_fitted) { SetCamera(w, l, t); _fitted = true; }
        _ = SimulateAsync(doc, report, w, l, t);
    }

    public void ResetView() { _fitted = false; _shownKey = null; }

    /// <summary>
    /// Shows the loaded program with its progress: moves before <paramref name="index"/> (and the part of that
    /// one up to <paramref name="fraction"/>) have run. <paramref name="toView"/> maps work X/Y to the view.
    /// Rapids are left to the rapids layer. <paramref name="tool"/> is the tool tip in work coordinates.
    /// </summary>
    public void ShowJob(IReadOnlyList<CNCStudio.Core.GCode.ProgramMove> moves, int index, double fraction,
        Func<double, double, Point> toView, double[]? tool)
    {
        var done = new Point3DCollection(); var todo = new Point3DCollection();
        Point3D P(double x, double y, double z) { var p = toView(x, y); return new Point3D(p.X, p.Y, z); }
        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            if (m.Rapid) continue;
            var a = P(m.X0, m.Y0, m.Z0); var b = P(m.X1, m.Y1, m.Z1);
            if (i < index) { done.Add(a); done.Add(b); }
            else if (i == index && fraction > 0)
            {
                var c = a + (b - a) * fraction;
                done.Add(a); done.Add(c); todo.Add(c); todo.Add(b);
            }
            else { todo.Add(a); todo.Add(b); }
        }
        _jobDone.Points = done; _jobTodo.Points = todo;
        _jobTool.Points = tool is { Length: >= 3 } t
            ? new Point3DCollection { P(t[0], t[1], t[2]), P(t[0], t[1], t[2] + 25) }
            : new Point3DCollection();
        if (!_jobActive) { _jobActive = true; Layer_Changed(this, new RoutedEventArgs()); }
    }

    /// <summary>No program loaded: back to the planned cuts.</summary>
    public void ClearJob()
    {
        if (!_jobActive) return;
        _jobActive = false;
        _jobDone.Points = new Point3DCollection(); _jobTodo.Points = new Point3DCollection(); _jobTool.Points = new Point3DCollection();
        Layer_Changed(this, new RoutedEventArgs());
    }

    /// <summary>A project without material: no stock, paths, fixtures or work zero – only the machine table.</summary>
    public void ShowEmpty(CncDocument doc)
    {
        _doc = doc;
        var key = $"empty:{doc.Machine.WorkWidthMm}:{doc.Machine.WorkLengthMm}:{doc.Machine.HomeCorner}";
        if (_shownKey == key) return;
        _simulation?.Cancel();
        _stock.Content = null; _fixtures.Children.Clear(); _zero.Children.Clear();
        _cuts.Points = new Point3DCollection(); _plunges.Points = new Point3DCollection(); _rapids.Points = new Point3DCollection();
        _stockW = _stockL = 0; _stockRuler.Children.Clear(); BuildSelectionMark();
        BuildTable(doc);
        Layer_Changed(this, new RoutedEventArgs());
        var a = MachineTable3D.For(doc);
        SetCamera(a.Width, a.Length, 0, a.Left, a.Front, fitAll: true);
        _fitted = false;     // the first project with material gets its own view
        _shownKey = key;
    }

    private string? _shownKey;

    private static string SceneKey(CncDocument doc, CamReport report)
    {
        var h = new HashCode();
        h.Add(doc.MaterialWidth); h.Add(doc.MaterialHeight); h.Add(doc.MaterialThickness); h.Add(doc.WorkZero);
        h.Add(doc.Stock.X); h.Add(doc.Stock.Y);
        h.Add(doc.Machine.WorkWidthMm); h.Add(doc.Machine.WorkLengthMm); h.Add(doc.Machine.HomeCorner);
        foreach (var f in doc.Fixtures) { h.Add(f.X); h.Add(f.Y); h.Add(f.Width); h.Add(f.Length); h.Add(f.HeightAboveStockMm); }
        foreach (var o in report.Operations) { h.Add(o.Operation.ToolDiameter); h.Add(o.Operation.ToolKind); h.Add(o.Operation.ToolAngleDeg); }
        foreach (var m in report.Motion.Moves)
        {
            h.Add(m.Kind); h.Add(m.X0); h.Add(m.Y0); h.Add(m.Z0); h.Add(m.X1); h.Add(m.Y1); h.Add(m.Z1);
            h.Add(m.OperationIndex); h.Add(m.PathIndex); h.Add(m.S0); h.Add(m.S1);
        }
        return $"{report.Motion.Moves.Count}:{h.ToHashCode()}";
    }

    private async Task SimulateAsync(CncDocument doc, CamReport report, double w, double l, double t)
    {
        _simulation?.Cancel();
        var cts = new CancellationTokenSource();
        _simulation = cts;
        InfoText.Text = "Simulerar material …";
        var started = DateTime.Now;
        StockHeightMap map;
        try { map = await Task.Run(() => StockSimulator.Simulate(doc, report), cts.Token); }
        catch (Exception ex) { InfoText.Text = "Simuleringen misslyckades: " + ex.Message; return; }
        if (cts.IsCancellationRequested) return;
        _lastMap = map;
        _stock.Content = BuildStockModel(map, StockOpacity);
        var ms = (DateTime.Now - started).TotalMilliseconds;
        InfoText.Text = $"Material {F(w)} × {F(l)} × {F(t)} mm · rutnät {F(map.Cell, 2)} mm · " +
                        $"djupast {F(-map.MinHeight, 2)} mm · bortfräst {F(map.RemovedVolume / 1000, 1)} cm³ · {ms:0} ms. " +
                        "Visuell uppskattning – inte en kontroll av G-coden.";
    }

    // ---- Material ----

    private StockHeightMap? _lastMap;

    /// <summary>Massivt = 1, Genomskinligt = see-through so the paths inside the material show.</summary>
    private double StockOpacity => StockMode?.SelectedIndex == 1 ? 0.35 : 1.0;

    private void StockMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_lastMap is not null) _stock.Content = BuildStockModel(_lastMap, StockOpacity);
        Layer_Changed(sender, e);
    }

    private static Model3DGroup BuildStockModel(StockHeightMap map, double opacity)
    {
        // Thin the grid for display, keeping the DEEPEST value in each block so no groove disappears.
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)map.Nx * map.Ny / MaxDisplayVertices)));
        int nx = (map.Nx + step - 1) / step, ny = (map.Ny + step - 1) / step;
        double cx = map.Width / nx, cy = map.Length / ny, t = map.Thickness;
        var h = new double[nx, ny];
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                double min = 0;
                for (int b = j * step; b < Math.Min(map.Ny, (j + 1) * step); b++)
                    for (int a = i * step; a < Math.Min(map.Nx, (i + 1) * step); a++)
                        min = Math.Min(min, map[a, b]);
                h[i, j] = min;
            }

        var top = new MeshGeometry3D();
        var pos = new Point3DCollection(nx * ny);
        var tex = new PointCollection(nx * ny);
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                pos.Add(new Point3D((i + 0.5) * cx, (j + 0.5) * cy, h[i, j]));
                tex.Add(new Point(Math.Clamp(-h[i, j] / t, 0, 1), 0.5)); // colour by depth
            }
        var idx = new Int32Collection((nx - 1) * (ny - 1) * 6);
        for (int j = 0; j + 1 < ny; j++)
            for (int i = 0; i + 1 < nx; i++)
            {
                int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                idx.Add(a); idx.Add(b); idx.Add(d);
                idx.Add(a); idx.Add(d); idx.Add(c);
            }
        top.Positions = pos; top.TextureCoordinates = tex; top.TriangleIndices = idx;
        Freeze(top);

        // Depth colouring: light wood at the surface, darker deeper, grey spoilboard where cut through.
        var depthBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        depthBrush.GradientStops.Add(new GradientStop(Color.FromRgb(231, 202, 158), 0.0));
        depthBrush.GradientStops.Add(new GradientStop(Color.FromRgb(196, 160, 112), 0.35));
        depthBrush.GradientStops.Add(new GradientStop(Color.FromRgb(140, 104, 66), 0.985));
        depthBrush.GradientStops.Add(new GradientStop(Color.FromRgb(62, 68, 76), 1.0));
        depthBrush.Opacity = opacity;
        depthBrush.Freeze();

        var group = new Model3DGroup();
        group.Children.Add(new GeometryModel3D(top, new DiffuseMaterial(depthBrush)) { BackMaterial = new DiffuseMaterial(depthBrush) });
        var side = new SolidColorBrush(Color.FromRgb(201, 165, 116)) { Opacity = opacity };
        group.Children.Add(new GeometryModel3D(Sides(h, nx, ny, cx, cy, t), new DiffuseMaterial(side)) { BackMaterial = new DiffuseMaterial(side) });
        // The underside, so the stock looks solid when the view is turned to look from below. Cells cut
        // through are left open, so a through-cut is still visible from underneath.
        var underside = new SolidColorBrush(Color.FromRgb(176, 140, 96)) { Opacity = opacity };
        group.Children.Add(new GeometryModel3D(Bottom(h, nx, ny, cx, cy, t), new DiffuseMaterial(underside)) { BackMaterial = new DiffuseMaterial(underside) });
        group.Freeze();
        return group;
    }

    /// <summary>The bottom face at −t on the same grid as the top, without the cells that are cut through.</summary>
    private static MeshGeometry3D Bottom(double[,] h, int nx, int ny, double cx, double cy, double t)
    {
        var m = new MeshGeometry3D();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
                m.Positions.Add(new Point3D((i + 0.5) * cx, (j + 0.5) * cy, -t));
        bool Through(int i, int j) => h[i, j] <= -t + 1e-4;
        for (int j = 0; j + 1 < ny; j++)
            for (int i = 0; i + 1 < nx; i++)
            {
                if (Through(i, j) || Through(i + 1, j) || Through(i, j + 1) || Through(i + 1, j + 1)) continue;
                int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                // Wound the other way than the top, so the face points down.
                m.TriangleIndices.Add(a); m.TriangleIndices.Add(d); m.TriangleIndices.Add(b);
                m.TriangleIndices.Add(a); m.TriangleIndices.Add(c); m.TriangleIndices.Add(d);
            }
        Freeze(m);
        return m;
    }

    /// <summary>The four outer walls from the bottom up to the (possibly cut) edge of the surface.</summary>
    private static MeshGeometry3D Sides(double[,] h, int nx, int ny, double cx, double cy, double t)
    {
        var m = new MeshGeometry3D();
        void Wall(Func<int, Point3D> top, int count)
        {
            for (int k = 0; k + 1 < count; k++)
            {
                var p0 = top(k); var p1 = top(k + 1);
                int n = m.Positions.Count;
                m.Positions.Add(new Point3D(p0.X, p0.Y, -t)); m.Positions.Add(new Point3D(p1.X, p1.Y, -t));
                m.Positions.Add(p1); m.Positions.Add(p0);
                m.TriangleIndices.Add(n); m.TriangleIndices.Add(n + 1); m.TriangleIndices.Add(n + 2);
                m.TriangleIndices.Add(n); m.TriangleIndices.Add(n + 2); m.TriangleIndices.Add(n + 3);
            }
        }
        Wall(i => new Point3D((i + 0.5) * cx, 0.5 * cy, h[i, 0]), nx);                  // front
        Wall(i => new Point3D((i + 0.5) * cx, (ny - 0.5) * cy, h[i, ny - 1]), nx);      // back
        Wall(j => new Point3D(0.5 * cx, (j + 0.5) * cy, h[0, j]), ny);                  // left
        Wall(j => new Point3D((nx - 0.5) * cx, (j + 0.5) * cy, h[nx - 1, j]), ny);      // right
        Freeze(m);
        return m;
    }

    private static void Freeze(Freezable f) { if (f.CanFreeze) f.Freeze(); }

    // ---- Toolpaths ----

    private void BuildPaths(CncDocument doc, CamReport report)
    {
        double l = doc.MaterialHeight;
        Point3D P(double x, double y, double z) => new(x, l - y, z);
        var cuts = new Point3DCollection(); var plunges = new Point3DCollection(); var rapids = new Point3DCollection();
        foreach (var m in report.Motion.Moves)
        {
            if (m.Kind == MoveKind.Rapid) { rapids.Add(P(m.X0, m.Y0, m.Z0)); rapids.Add(P(m.X1, m.Y1, m.Z1)); continue; }
            if (!m.FollowsPath)
            {
                var target = m.MovesXY ? cuts : plunges;
                target.Add(P(m.X0, m.Y0, m.Z0)); target.Add(P(m.X1, m.Y1, m.Z1));
                continue;
            }
            var r = report.Operations[m.OperationIndex];
            if (r.Preview!.Is3D)
            {
                var p3 = r.Preview.Centerlines[m.PathIndex]; var pz = r.Preview.PathZ[m.PathIndex];
                for (int i = 1; i < p3.Count; i++) { cuts.Add(P(p3[i - 1].X, p3[i - 1].Y, pz[i - 1])); cuts.Add(P(p3[i].X, p3[i].Y, pz[i])); }
                continue;
            }
            var path = r.Preview!.Centerlines.Count > 0 ? r.Preview.Centerlines[m.PathIndex] : r.Preview.Centerline;
            var slice = PathMath.Slice(path, m.S0, m.S1);
            double total = PathMath.Length(slice), run = 0;
            for (int i = 1; i < slice.Count; i++)
            {
                double z0 = total > 0 ? m.Z0 + (m.Z1 - m.Z0) * run / total : m.Z1;
                run += PathMath.Distance(slice[i - 1], slice[i]);
                double z1 = total > 0 ? m.Z0 + (m.Z1 - m.Z0) * run / total : m.Z1;
                cuts.Add(P(slice[i - 1].X, slice[i - 1].Y, z0)); cuts.Add(P(slice[i].X, slice[i].Y, z1));
            }
        }
        _cuts.Points = cuts; _plunges.Points = plunges; _rapids.Points = rapids;
    }

    private void BuildFixtures(CncDocument doc)
    {
        _fixtures.Children.Clear();
        double l = doc.MaterialHeight;
        foreach (var f in doc.Fixtures)
        {
            double x = f.X - doc.Stock.X, y = f.Y - doc.Stock.Y; // stock-local design coordinates
            _fixtures.Children.Add(new BoxVisual3D
            {
                Center = new Point3D(x + f.Width / 2, l - (y + f.Length / 2), f.HeightAboveStockMm / 2),
                Length = f.Width, Width = f.Length, Height = Math.Max(0.5, f.HeightAboveStockMm),
                Fill = new SolidColorBrush(Color.FromArgb(200, 214, 110, 60))
            });
        }
    }

    private void BuildZero(CncDocument doc)
    {
        _zero.Children.Clear();
        var (ox, oy) = doc.WorkZero == WorkZero.Center ? (doc.MaterialWidth / 2, doc.MaterialHeight / 2) : (0.0, 0.0);
        double len = Math.Max(15, Math.Min(doc.MaterialWidth, doc.MaterialHeight) * 0.08);
        var o = new Point3D(ox, oy, 0.2);
        _zero.Children.Add(new LinesVisual3D { Color = Colors.Red, Thickness = 3, Points = new Point3DCollection { o, new Point3D(ox + len, oy, 0.2) } });
        _zero.Children.Add(new LinesVisual3D { Color = Colors.LimeGreen, Thickness = 3, Points = new Point3DCollection { o, new Point3D(ox, oy + len, 0.2) } });
        _zero.Children.Add(new LinesVisual3D { Color = Color.FromRgb(70, 150, 255), Thickness = 3, Points = new Point3DCollection { o, new Point3D(ox, oy, len) } });
    }

    // ---- Machine table ----

    private double _tableTopZ, _stockW, _stockL;

    private static readonly Color TableTop = Color.FromRgb(0x2A, 0x3B, 0x4D), TableEdge = Color.FromRgb(0x8C, 0xA3, 0xBA),
        TableGrid = Color.FromRgb(0x3E, 0x55, 0x6D), Home = Color.FromRgb(0x7C, 0xD9, 0x9C);

    /// <summary>The machine's work area under the material: a table top, grid lines from the home corner, its edge and HEM.</summary>
    private void BuildTable(CncDocument doc)
    {
        _table.Children.Clear();
        var a = MachineTable3D.For(doc);
        _tableTopZ = a.TopZ;
        if (!(a.Width > 0 && a.Length > 0)) return;
        // Just under the material's underside, so the two never fight over the same depth.
        double z = a.TopZ - 0.3;
        _table.Children.Add(new BoxVisual3D
        {
            Center = new Point3D(a.Left + a.Width / 2, a.Front + a.Length / 2, z - 3),
            Length = a.Width, Width = a.Length, Height = 6, Fill = new SolidColorBrush(TableTop)
        });
        // The same grid as the 2D views: 10 mm faint, 50 mm medium, 100 mm strong, from the table's back-left corner.
        var (major, medium, minor) = MachineTable3D.TableGrid(a);
        void AddGrid(List<(double X0, double Y0, double X1, double Y1)> lines, byte alpha, double thickness, double lift)
        {
            var points = new Point3DCollection();
            foreach (var (x0, y0, x1, y1) in lines) { points.Add(new Point3D(x0, y0, z + lift)); points.Add(new Point3D(x1, y1, z + lift)); }
            _table.Children.Add(new LinesVisual3D { Color = Color.FromArgb(alpha, 0x7F, 0xA3, 0xC6), Thickness = thickness, Points = points });
        }
        AddGrid(minor, 40, 0.6, 0.03);
        AddGrid(medium, 90, 0.9, 0.05);
        AddGrid(major, 150, 1.2, 0.07);
        _table.Children.Add(new LinesVisual3D
        {
            Color = TableEdge, Thickness = 1.5, Points = new Point3DCollection
            {
                new(a.Left, a.Front, z + 0.1), new(a.Right, a.Front, z + 0.1), new(a.Right, a.Front, z + 0.1), new(a.Right, a.Back, z + 0.1),
                new(a.Right, a.Back, z + 0.1), new(a.Left, a.Back, z + 0.1), new(a.Left, a.Back, z + 0.1), new(a.Left, a.Front, z + 0.1)
            }
        });
        var (hx, hy) = MachineTable3D.HomePoint(a, doc.Machine.HomeCorner);
        _table.Children.Add(new SphereVisual3D { Center = new Point3D(hx, hy, z + 2), Radius = 5, Fill = new SolidColorBrush(Home) });
        _table.Children.Add(new BillboardTextVisual3D
        {
            Text = "HEM", Position = new Point3D(hx, hy, z + 18), Foreground = new SolidColorBrush(Home), Background = Brushes.Transparent,
            FontWeight = FontWeights.Bold, FontSize = 13
        });
        InfoText.Text = $"Maskinbord {F(a.Width)} × {F(a.Length)} mm, rutor 10/50/100 mm som i 2D.";
        BuildTableRuler(doc, a, z);
    }

    // ---- Live tool position ----

    private static readonly Color ToolRed = Color.FromRgb(0xFF, 0x5A, 0x5A);

    /// <summary>
    /// The tool where the machine says it is (null = unknown: not connected, not homed or in alarm), like the
    /// cross in the 2D machine view: a cross on the table under the tool, a line up to the tip and the tool
    /// itself above it. With an unknown height only the cross on the table is drawn.
    /// </summary>
    public void ShowTool(ToolPosition? tool)
    {
        _tool.Children.Clear();
        _toolShown = tool is not null;
        if (tool is { } t)
        {
            // On the material's top when the tool is over it, otherwise on the table – where the 2D cross would be seen.
            bool overStock = t.X >= 0 && t.X <= _stockW && t.Y >= 0 && t.Y <= _stockL;
            double table = (overStock ? 0 : _tableTopZ) + 0.3, arm = 14;
            var red = new LinesVisual3D { Color = ToolRed, Thickness = 2, Points = new Point3DCollection
            {
                new(t.X - arm, t.Y, table), new(t.X + arm, t.Y, table), new(t.X, t.Y - arm, table), new(t.X, t.Y + arm, table)
            } };
            _tool.Children.Add(red);
            if (t.Z is double z)
            {
                // A thin line from the table up to the tip, so the height can be read against the material.
                _tool.Children.Add(new LinesVisual3D { Color = Color.FromArgb(160, 0xFF, 0x5A, 0x5A), Thickness = 1,
                    Points = new Point3DCollection { new(t.X, t.Y, table), new(t.X, t.Y, z) } });
                _tool.Children.Add(new TruncatedConeVisual3D
                {
                    Origin = new Point3D(t.X, t.Y, z), Normal = new Vector3D(0, 0, 1), BaseRadius = 0.5, TopRadius = 3.2, Height = 8,
                    Fill = new SolidColorBrush(ToolRed)
                });
                _tool.Children.Add(new PipeVisual3D
                {
                    Point1 = new Point3D(t.X, t.Y, z + 8), Point2 = new Point3D(t.X, t.Y, z + 40), Diameter = 6.4,
                    Fill = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8))
                });
            }
        }
        Layer_Changed(this, new RoutedEventArgs());
    }

    // ---- Rulers ----

    /// <summary>A ruler box was ticked here: (material, table). The main window keeps the setting and the 2D rulers in step.</summary>
    public event Action<bool, bool>? RulersChanged;
    private bool _settingRulers;

    /// <summary>Shows or hides the material's and the table's rulers (from the setting or the Visa menu).</summary>
    public void SetRulers(bool stock, bool table)
    {
        _settingRulers = true;
        ShowStockRulerBox.IsChecked = stock; ShowTableRulerBox.IsChecked = table;
        _settingRulers = false;
        Layer_Changed(this, new RoutedEventArgs());
    }

    private void Ruler_Changed(object sender, RoutedEventArgs e)
    {
        Layer_Changed(sender, e);
        if (!_settingRulers && ShowStockRulerBox is not null && ShowTableRulerBox is not null)
            RulersChanged?.Invoke(ShowStockRulerBox.IsChecked == true, ShowTableRulerBox.IsChecked == true);
    }

    private static readonly Color TableRulerColor = Color.FromRgb(0x9E, 0xB7, 0xD1), StockRulerColor = Color.FromRgb(0xE0, 0xB8, 0x84),
        RulerZero = Color.FromRgb(0x6F, 0xB6, 0xFF);

    /// <summary>
    /// The table's rulers, outside its front and left edges: mm from the home corner, as in Maskin's 2D rulers and
    /// the fields under Material på bordet.
    /// </summary>
    private void BuildTableRuler(CncDocument doc, MachineTable3D.Area a, double z)
    {
        _tableRuler.Children.Clear();
        bool left = doc.Machine.HomeCorner is HomeCorner.BackLeft or HomeCorner.FrontLeft;
        bool front = doc.Machine.HomeCorner is HomeCorner.FrontLeft or HomeCorner.FrontRight;
        // value = sign·position + offset along each edge
        double sx = left ? 1 : -1, ox = left ? -a.Left : a.Right;
        double sy = front ? 1 : -1, oy = front ? -a.Front : a.Back;
        AddRuler(_tableRuler, TableRulerColor, z + 0.1, alongX: true, edge: a.Front, outward: -1, from: a.Left, to: a.Right, sign: sx, offset: ox, size: 1.4);
        AddRuler(_tableRuler, TableRulerColor, z + 0.1, alongX: false, edge: a.Left, outward: -1, from: a.Front, to: a.Back, sign: sy, offset: oy, size: 1.4);
    }

    /// <summary>The material's rulers, just outside its front and left edges at the top: work coordinates (0 = X0 Y0).</summary>
    private void BuildStockRuler(CncDocument doc)
    {
        _stockRuler.Children.Clear();
        if (!doc.HasMaterial) return;
        double w = doc.MaterialWidth, l = doc.MaterialHeight;
        double ox = doc.WorkZero == WorkZero.Center ? -w / 2 : 0, oy = doc.WorkZero == WorkZero.Center ? -l / 2 : 0;
        AddRuler(_stockRuler, StockRulerColor, 0.2, alongX: true, edge: 0, outward: -1, from: 0, to: w, sign: 1, offset: ox, size: 1);
        AddRuler(_stockRuler, StockRulerColor, 0.2, alongX: false, edge: 0, outward: -1, from: 0, to: l, sign: 1, offset: oy, size: 1);
    }

    /// <summary>
    /// One ruler along an edge (alongX: the edge y = <paramref name="edge"/> from x = from to to; otherwise the edge
    /// x = edge from y = from to to). Ticks point outwards; the value at a position p is sign·p + offset.
    /// </summary>
    private static void AddRuler(ModelVisual3D target, Color color, double z, bool alongX, double edge, int outward,
        double from, double to, double sign, double offset, double size)
    {
        double length = Math.Abs(to - from);
        double step = RulerTicks.LabelStep(length);
        double gap = 2 * size, minorLen = 3 * size, mediumLen = 5 * size, majorLen = 8 * size;
        var line = new Point3DCollection(); var zero = new Point3DCollection();
        Point3D P(double along, double across) => alongX ? new Point3D(along, edge + outward * across, z) : new Point3D(edge + outward * across, along, z);
        line.Add(P(from, gap)); line.Add(P(to, gap));
        foreach (var t in RulerTicks.Between(sign * from + offset, sign * to + offset, step))
        {
            double p = (t.Value - offset) / sign;
            double len = t.Labelled ? majorLen : t.Medium ? mediumLen : minorLen;
            var target2 = t.Value == 0 ? zero : line;
            target2.Add(P(p, gap)); target2.Add(P(p, gap + len));
            if (t.Labelled)
                target.Children.Add(new BillboardTextVisual3D
                {
                    Text = t.Value.ToString("0.##", CultureInfo.CurrentCulture), Position = P(p, gap + majorLen + 5 * size),
                    Foreground = new SolidColorBrush(t.Value == 0 ? RulerZero : color), Background = Brushes.Transparent,
                    FontSize = 11, FontWeight = t.Value == 0 ? FontWeights.Bold : FontWeights.Normal
                });
        }
        target.Children.Add(new LinesVisual3D { Color = color, Thickness = 1, Points = line });
        if (zero.Count > 0) target.Children.Add(new LinesVisual3D { Color = RulerZero, Thickness = 2, Points = zero });
    }

    // ---- View ----

    private void SetCamera(double w, double l, double t, double x0 = 0, double y0 = 0, bool fitAll = false)
    {
        var target = new Point3D(x0 + w / 2, y0 + l / 2, -t / 2);
        double d = Math.Max(w, l) * 1.4;
        var position = new Point3D(target.X - d * 0.35, y0 - d * 0.75, d * 0.75);
        Scene.Camera = new PerspectiveCamera(position, target - position, new Vector3D(0, 0, 1), 45);
        Fit(0, includeTable: fitAll);
    }

    /// <summary>Zooms to what is shown; the table only when asked, so the material stays the focus.</summary>
    private void Fit(double ms, bool includeTable)
    {
        bool hide = !includeTable && Scene.Children.Contains(_table);
        bool hideRuler = !includeTable && Scene.Children.Contains(_tableRuler);
        if (hide) Scene.Children.Remove(_table);
        if (hideRuler) Scene.Children.Remove(_tableRuler);
        Scene.ZoomExtents(ms);
        if (hide) Scene.Children.Insert(0, _table);
        if (hideRuler) Scene.Children.Add(_tableRuler);
    }

    private void Layer_Changed(object sender, RoutedEventArgs e)
    {
        // Checked events fire while the XAML is still loading, before every checkbox exists.
        if (StockMode is null || ShowCuts is null || ShowRapids is null || ShowFixtures is null || Scene is null) return;
        SetVisible(_stock, StockMode.SelectedIndex != 2);
        // While a program is loaded its own path replaces the planned cuts (same moves, with progress).
        SetVisible(_cuts, ShowCuts.IsChecked == true && !_jobActive);
        SetVisible(_plunges, ShowCuts.IsChecked == true && !_jobActive);
        SetVisible(_jobTodo, ShowCuts.IsChecked == true && _jobActive);
        SetVisible(_jobDone, ShowCuts.IsChecked == true && _jobActive);
        // The live tool replaces the job's red bar when it is shown (both follow the status reports).
        bool liveTool = _toolShown && ShowToolPos?.IsChecked == true;
        SetVisible(_tool, liveTool);
        SetVisible(_jobTool, _jobActive && !liveTool);
        SetVisible(_rapids, ShowRapids.IsChecked == true);
        SetVisible(_fixtures, ShowFixtures.IsChecked == true);
        SetVisible(_table, ShowTable?.IsChecked == true);
        SetVisible(_tableRuler, ShowTableRulerBox?.IsChecked == true && ShowTable?.IsChecked == true);
        SetVisible(_stockRuler, ShowStockRulerBox?.IsChecked == true);
        // See-through material must be drawn after the paths, or it hides them in the depth buffer.
        if (StockMode.SelectedIndex == 1 && Scene.Children.Contains(_stock)) { Scene.Children.Remove(_stock); Scene.Children.Add(_stock); }
    }

    private void SetVisible(Visual3D v, bool on)
    {
        if (on && !Scene.Children.Contains(v)) Scene.Children.Add(v);
        if (!on && Scene.Children.Contains(v)) Scene.Children.Remove(v);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Fit(300, includeTable: _shownKey?.StartsWith("empty") == true);

    private void FitTable_Click(object sender, RoutedEventArgs e) => Fit(300, includeTable: true);

    private static string F(double v, int d = 0) => v.ToString(d == 0 ? "0" : "0." + new string('#', d), CultureInfo.InvariantCulture);
}
