using System.Windows;
using System.Windows.Media;
using CNCStudio.Core;
using CNCStudio.Core.GCode;
using CNCStudio.Core.Grbl;
using WpfPath = System.Windows.Shapes.Path;

namespace CNCStudio.Desktop;

/// <summary>
/// The loaded program drawn in the machine view: what is still to run in bright blue, what has run in
/// orange, so it is clear where the machine is in the program. The split point follows the tool's real
/// position (status reports), not the lines sent – GRBL accepts lines well before it runs them.
/// </summary>
public partial class MainWindow
{
    private static readonly Brush JobTodoBrush = FrozenBrush(0x4F, 0xB3, 0xFF), JobDoneBrush = FrozenBrush(0xFF, 0x9F, 0x3A),
        JobRapidBrush = FrozenBrush(0x8C, 0xA3, 0xBA);

    private static Brush FrozenBrush(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }

    private IReadOnlyList<string>? _jobPathSource;
    private IReadOnlyList<ProgramMove> _jobPath = Array.Empty<ProgramMove>();
    /// <summary>Segments before this index have run; the one at it has run up to <see cref="_jobPathFraction"/>.</summary>
    private int _jobPathIndex;
    private double _jobPathFraction;
    private WpfPath? _jobDonePath, _jobTodoPath, _jobRapidPath;

    /// <summary>The program shown: the loaded one (null when none is loaded).</summary>
    private IReadOnlyList<ProgramMove> JobPath
    {
        get
        {
            if (!ReferenceEquals(_jobPathSource, _jobLines))
            {
                _jobPathSource = _jobLines;
                _jobPath = _jobLines is null ? Array.Empty<ProgramMove>() : GCodePath.Parse(_jobLines);
                _jobPathIndex = 0; _jobPathFraction = 0;
            }
            return _jobPath;
        }
    }

    private bool HasJobPath => JobPath.Count > 0;

    /// <summary>Adds the program's path to the machine view (called from RenderMachine).</summary>
    private void AddJobPath()
    {
        _jobDonePath = new WpfPath { Stroke = JobDoneBrush, StrokeThickness = 1.6 / _zoom, IsHitTestVisible = false,
            StrokeLineJoin = PenLineJoin.Round, ToolTip = "Körd del av programmet" };
        _jobTodoPath = new WpfPath { Stroke = JobTodoBrush, StrokeThickness = 1.2 / _zoom, IsHitTestVisible = false,
            StrokeLineJoin = PenLineJoin.Round, ToolTip = "Återstår" };
        _jobRapidPath = new WpfPath { Stroke = JobRapidBrush, StrokeThickness = 0.8 / _zoom, IsHitTestVisible = false,
            StrokeDashArray = new DoubleCollection { 3, 3 }, Opacity = 0.7 };
        MachineCanvas.Children.Add(_jobRapidPath);
        MachineCanvas.Children.Add(_jobTodoPath);
        MachineCanvas.Children.Add(_jobDonePath);
        UpdateJobPathGeometry();
    }

    /// <summary>Work coordinates → machine view (stock-local design coordinates moved onto the table).</summary>
    private Point WorkToMachineView(double x, double y)
    {
        var (dx, dy) = _document.FromWork(x, y);
        return new Point(dx + _document.Stock.X, dy + _document.Stock.Y);
    }

    private void UpdateJobPathGeometry()
    {
        if (_jobDonePath is null || _jobTodoPath is null || _jobRapidPath is null) return;
        var path = JobPath;
        var done = new StreamGeometry(); var todo = new StreamGeometry(); var rapid = new StreamGeometry();
        using (var d = done.Open())
        using (var t = todo.Open())
        using (var r = rapid.Open())
        {
            for (int i = 0; i < path.Count; i++)
            {
                var s = path[i];
                var a = WorkToMachineView(s.X0, s.Y0); var b = WorkToMachineView(s.X1, s.Y1);
                if ((a - b).Length < 1e-9) continue;               // a plunge or lift: nothing to draw in XY
                if (s.Rapid) { r.BeginFigure(a, false, false); r.LineTo(b, true, false); continue; }
                if (i < _jobPathIndex) { d.BeginFigure(a, false, false); d.LineTo(b, true, false); }
                else if (i == _jobPathIndex && _jobPathFraction > 0)
                {
                    var m = a + (b - a) * _jobPathFraction;
                    d.BeginFigure(a, false, false); d.LineTo(m, true, false);
                    t.BeginFigure(m, false, false); t.LineTo(b, true, false);
                }
                else { t.BeginFigure(a, false, false); t.LineTo(b, true, false); }
            }
        }
        done.Freeze(); todo.Freeze(); rapid.Freeze();
        _jobDonePath.Data = done; _jobTodoPath.Data = todo; _jobRapidPath.Data = rapid;
        Update3DJob();
    }

    /// <summary>The tool tip in work coordinates from the last status report during a run.</summary>
    private double[]? _jobToolWork;

    /// <summary>The same progress in the 3D view (when it is open).</summary>
    private void Update3DJob()
    {
        if (!_show3D || View3DPanel is null) return;
        if (!HasJobPath) { View3DPanel.ClearJob(); return; }
        double l = _document.MaterialHeight;
        View3DPanel.ShowJob(JobPath, _jobPathIndex, _jobPathFraction,
            (x, y) => { var (dx, dy) = _document.FromWork(x, y); return new Point(dx, l - dy); }, _jobToolWork);
    }

    /// <summary>
    /// Moves the run/remaining split to where the tool is: the first segment from the current one onwards
    /// (not past the lines GRBL has accepted) that passes through the tool position.
    /// </summary>
    private void UpdateJobPathPosition(double[] wpos)
    {
        var path = JobPath;
        if (path.Count == 0 || _runner is not JobRunner runner || wpos.Length < 3) return;
        _jobToolWork = (double[])wpos.Clone();
        int acked = runner.AckedLines;
        int bestIndex = -1; double bestDist = double.MaxValue, bestT = 0;
        for (int i = _jobPathIndex; i < path.Count && path[i].LineIndex <= acked; i++)
        {
            var (dist, t) = Distance(path[i], wpos);
            if (dist < bestDist - 1e-6) { bestDist = dist; bestIndex = i; bestT = t; }
            if (dist < 0.05) break;                                 // on this segment: earliest match wins
        }
        if (bestIndex < 0 || bestDist > 2 || bestIndex == _jobPathIndex && Math.Abs(bestT - _jobPathFraction) < 0.01)
        { Update3DJob(); return; }                                  // only the tool moved (or not on the path)
        _jobPathIndex = bestIndex; _jobPathFraction = bestT;
        UpdateJobPathGeometry();
    }

    /// <summary>When a program has finished: all of it has run.</summary>
    private void MarkJobPathDone()
    {
        _jobPathIndex = JobPath.Count; _jobPathFraction = 0;
        UpdateJobPathGeometry();
    }

    private static (double Dist, double T) Distance(ProgramMove s, double[] p)
    {
        double vx = s.X1 - s.X0, vy = s.Y1 - s.Y0, vz = s.Z1 - s.Z0;
        double len2 = vx * vx + vy * vy + vz * vz;
        double t = len2 < 1e-12 ? 0 : Math.Clamp(((p[0] - s.X0) * vx + (p[1] - s.Y0) * vy + (p[2] - s.Z0) * vz) / len2, 0, 1);
        double dx = s.X0 + vx * t - p[0], dy = s.Y0 + vy * t - p[1], dz = s.Z0 + vz * t - p[2];
        return (Math.Sqrt(dx * dx + dy * dy + dz * dz), t);
    }
}
