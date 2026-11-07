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
/// front-left top corner): simulated material (Easel style) and toolpaths (gSender style).
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
    // The running program: what has run (orange), what is left (blue) and where the tool is.
    private readonly LinesVisual3D _jobDone = new() { Color = Color.FromRgb(255, 159, 58), Thickness = 2.0 };
    private readonly LinesVisual3D _jobTodo = new() { Color = Color.FromRgb(79, 179, 255), Thickness = 1.4 };
    private readonly LinesVisual3D _jobTool = new() { Color = Color.FromRgb(255, 77, 77), Thickness = 4 };
    private bool _jobActive;
    private CancellationTokenSource? _simulation;
    private bool _fitted;

    public View3DPanel()
    {
        InitializeComponent();
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
        var key = SceneKey(doc, report);
        if (key == _shownKey && _fitted) return;
        _shownKey = key;
        double w = doc.MaterialWidth, l = doc.MaterialHeight, t = doc.MaterialThickness;
        BuildPaths(doc, report);
        BuildFixtures(doc);
        BuildZero(doc);
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

    /// <summary>A project without material: no stock, paths, fixtures or work zero.</summary>
    public void ShowEmpty()
    {
        if (_shownKey == "empty") return;
        _simulation?.Cancel();
        _stock.Content = null; _fixtures.Content = null; _zero.Content = null;
        _cuts.Points = new Point3DCollection(); _plunges.Points = new Point3DCollection(); _rapids.Points = new Point3DCollection();
        _shownKey = "empty";
    }

    private string? _shownKey;

    private static string SceneKey(CncDocument doc, CamReport report)
    {
        var h = new HashCode();
        h.Add(doc.MaterialWidth); h.Add(doc.MaterialHeight); h.Add(doc.MaterialThickness); h.Add(doc.WorkZero);
        h.Add(doc.Stock.X); h.Add(doc.Stock.Y);
        foreach (var f in doc.Fixtures) { h.Add(f.X); h.Add(f.Y); h.Add(f.Width); h.Add(f.Length); h.Add(f.HeightAboveStockMm); }
        foreach (var o in report.Operations) { h.Add(o.Operation.ToolDiameter); h.Add(o.Operation.ToolKind); }
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

    // ---- View ----

    private void SetCamera(double w, double l, double t)
    {
        var target = new Point3D(w / 2, l / 2, -t / 2);
        double d = Math.Max(w, l) * 1.4;
        var position = new Point3D(w / 2 - d * 0.35, -d * 0.75, d * 0.75);
        Scene.Camera = new PerspectiveCamera(position, target - position, new Vector3D(0, 0, 1), 45);
        Scene.ZoomExtents(0);
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
        SetVisible(_jobTool, _jobActive);
        SetVisible(_rapids, ShowRapids.IsChecked == true);
        SetVisible(_fixtures, ShowFixtures.IsChecked == true);
        // See-through material must be drawn after the paths, or it hides them in the depth buffer.
        if (StockMode.SelectedIndex == 1 && Scene.Children.Contains(_stock)) { Scene.Children.Remove(_stock); Scene.Children.Add(_stock); }
    }

    private void SetVisible(Visual3D v, bool on)
    {
        if (on && !Scene.Children.Contains(v)) Scene.Children.Add(v);
        if (!on && Scene.Children.Contains(v)) Scene.Children.Remove(v);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Scene.ZoomExtents(300);

    private static string F(double v, int d = 0) => v.ToString(d == 0 ? "0" : "0." + new string('#', d), CultureInfo.InvariantCulture);
}
