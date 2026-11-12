using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Maskiner → (the connected machine) → Kontrollera vinkel (XY-squaring): the machine moves along X and
/// then along Y, the user marks the three points and measures the distances between them, and
/// <see cref="SquaringCheck"/> tells how far off 90° the axes are and which end of the gantry to move.
/// Moves are $J= jogs checked against the machine's limits. Nothing is written to the machine.
/// Every result is kept per machine, so a check after an adjustment can be compared with the one before.
/// </summary>
public sealed class SquaringWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Good = B(0x7C, 0xD9, 0x9C), Bad = B(0xFF, 0x7A, 0x86), Warn = B(0xFF, 0xD3, 0x9F), Line = B(0x5E, 0xB0, 0xFF);

    private readonly GrblClient _grbl;
    private readonly Func<double, double, double, string?> _checkJog;
    private readonly string _logPath;
    private readonly SquaringLog _log;
    private readonly TextBox _distX = Box(), _distY = Box(), _feed = Box(), _span = Box(), _a = Box(), _b = Box(), _c = Box();
    private readonly ComboBox _dirX = new() { Margin = new Thickness(0, 2, 0, 0) }, _dirY = new() { Margin = new Thickness(0, 2, 0, 0) };
    private readonly Button _moveX = new(), _moveY = new(), _back = new() { Content = "Tillbaka till punkt 1" }, _compute = new() { Content = "Räkna ut" };
    private readonly TextBlock _step = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ink, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ink, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Soft, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _history = new();
    private readonly Canvas _sketch = new() { Width = 170, Height = 130, Margin = new Thickness(12, 0, 0, 0) };
    /// <summary>0 = at point 1, 1 = at point 2 (after X), 2 = at point 3 (after Y).</summary>
    private int _at;
    /// <summary>The moves actually made (with sign), for the result; null before the Y move.</summary>
    private (double X, double Y)? _moved;
    private bool _busy;

    public SquaringWindow(GrblClient grbl, MachineProfile machine, Func<double, double, double, string?> checkJog, string logPath)
    {
        _grbl = grbl; _checkJog = checkJog; _logPath = logPath;
        _log = SquaringLog.Load(logPath);
        Title = $"Kontrollera vinkel (XY-squaring) – {machine.Name}";
        Width = 760; SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height - 20; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        // A big triangle measures the angle better: start at 80 % of the travel, in whole 50 mm.
        _distX.Text = F(Math.Max(100, Math.Floor(machine.WorkWidthMm * 0.8 / 50) * 50));
        _distY.Text = F(Math.Max(100, Math.Floor(machine.WorkLengthMm * 0.8 / 50) * 50));
        _feed.Text = "1500";
        _span.Text = F(_log.GantrySpan ?? machine.WorkWidthMm);
        _span.ToolTip = "Avståndet mellan de två Y-skenorna, alltså hur långt gantryt är. Ungefär räcker – det används för att räkna ut hur mycket ena änden ska flyttas.";
        foreach (var t in new[] { "åt höger (X+)", "åt vänster (X−)" }) _dirX.Items.Add(t);
        foreach (var t in new[] { "bakåt, bort från dig (Y+)", "framåt, mot dig (Y−)" }) _dirY.Items.Add(t);
        _dirX.SelectedIndex = 0; _dirY.SelectedIndex = 0;
        AutoDirections();
        _dirX.SelectionChanged += (_, _) => DirectionsChanged();
        _dirY.SelectionChanged += (_, _) => DirectionsChanged();
        _distX.TextChanged += (_, _) => UpdateButtons();
        _distY.TextChanged += (_, _) => UpdateButtons();

        _moveX.Click += async (_, _) => await MoveX();
        _moveY.Click += async (_, _) => await MoveY();
        _back.Click += async (_, _) => await MoveBack();
        _compute.Click += (_, _) => Compute();
        var close = new Button { Content = "Stäng", IsCancel = true };
        close.Click += (_, _) => Close();
        foreach (var b in new[] { _moveX, _moveY, _back, _compute, close })
        {
            b.Padding = new Thickness(12, 6, 12, 6); b.Margin = new Thickness(0, 0, 8, 0); b.Foreground = Brushes.White; b.BorderBrush = B(0x50, 0x67, 0x83);
            b.Background = b == _moveX || b == _moveY ? B(0x24, 0x61, 0x8C) : b == _compute ? B(0x2E, 0x7A, 0x4A) : B(0x34, 0x47, 0x5A);
        }

        var steps = new DockPanel();
        DockPanel.SetDock(_sketch, Dock.Right); steps.Children.Add(_sketch);
        steps.Children.Add(new StackPanel { Children =
        {
            _step,
            new TextBlock { Text = "Märk ut punkterna med en bit maskeringstejp och ett tunt kryss precis under fräsens spets. Sänk spetsen (gärna en V-fräs) så att den nästan nuddar tejpen, men inte ända ned – fräsen ska kunna flytta fritt. Snurra inte spindeln.",
                Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) },
            new WrapPanel { Children = { _moveX, _moveY, _back } },
        } });

        var content = new StackPanel { Margin = new Thickness(18), Children =
        {
            new TextBlock { Text = "KONTROLLERA VINKEL (XY-SQUARING)", FontSize = 18, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Maskinen flyttar längs X och sedan längs Y. Du märker ut tre punkter och mäter avstånden mellan dem. " +
                                   "Är X och Y inte exakt vinkelräta blir diagonalen fel, och CNC Studio räknar ut hur mycket ena änden av gantryt ska flyttas. " +
                                   "Inget skrivs till maskinen.", Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) },
            Card1(new StackPanel { Children =
            {
                Label("1 · TRIANGELN"),
                Row(("Sträcka X (mm)", _distX), ("Sträcka Y (mm)", _distY), ("Hastighet (mm/min)", _feed)),
                Row(("X går", _dirX), ("Y går", _dirY), ("Mellan Y-skenorna (mm)", _span)),
            } }),
            Card1(new StackPanel { Children = { Label("2 · MÄRK OCH FLYTTA"), steps } }),
            Card1(new StackPanel { Children =
            {
                Label("3 · MÄT"),
                new TextBlock { Text = "Mät mellan kryssens mitt med ett stålmåttband eller en lång linjal, så noggrant du kan.", Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) },
                Row(("1–2 längs X (mm)", _a), ("2–3 längs Y (mm)", _b), ("1–3 diagonalen (mm)", _c)),
                new WrapPanel { Margin = new Thickness(0, 6, 0, 0), Children = { _compute } },
                _result,
            } }),
            Card1(new StackPanel { Children = { Label("TIDIGARE KONTROLLER AV DEN HÄR MASKINEN"), _history } }),
            _status,
            new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { close } },
        } };
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
        UpdateButtons();
        ShowHistory();
    }

    private int Sx => _dirX.SelectedIndex == 1 ? -1 : +1;
    private int Sy => _dirY.SelectedIndex == 1 ? -1 : +1;

    /// <summary>Right and back if the triangle fits there, otherwise the way it fits.</summary>
    private void AutoDirections()
    {
        if (!P(_distX, out var dx) || !P(_distY, out var dy)) return;
        var (sx, sy, problem) = SquaringCheck.ChooseDirections(dx, dy, (x, y) => _checkJog(x, y, 0));
        if (problem is not null) { Say("Triangeln får inte plats härifrån: " + problem + " Jogga fräsen närmare mitten eller minska sträckorna.", Warn); return; }
        _dirX.SelectedIndex = sx > 0 ? 0 : 1;
        _dirY.SelectedIndex = sy > 0 ? 0 : 1;
    }

    private void DirectionsChanged()
    {
        if (_at != 0) Say("Riktningen ändrades efter att maskinen har flyttat – gå tillbaka till punkt 1 och börja om.", Warn);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (_moveX is null || _dirX.SelectedIndex < 0) return;
        _moveX.Content = $"Flytta X {(Sx > 0 ? "+" : "−")}{_distX.Text} mm";
        _moveY.Content = $"Flytta Y {(Sy > 0 ? "+" : "−")}{_distY.Text} mm";
        _moveX.IsEnabled = !_busy && _at == 0;
        _moveY.IsEnabled = !_busy && _at == 1;
        _back.IsEnabled = !_busy && _at > 0;
        _distX.IsEnabled = _distY.IsEnabled = _dirX.IsEnabled = _dirY.IsEnabled = !_busy && _at == 0;
        _step.Text = _at switch
        {
            0 => "Steg 1: märk PUNKT 1 under spetsen. Klicka sedan Flytta X.",
            1 => "Steg 2: märk PUNKT 2 under spetsen. Klicka sedan Flytta Y.",
            _ => "Steg 3: märk PUNKT 3 under spetsen. Mät sedan de tre avstånden under 3."
        };
        DrawSketch();
    }

    private async Task MoveX()
    {
        if (!P(_distX, out var dx) || dx < 50 || !P(_distY, out var dy) || dy < 50 || !P(_feed, out var f) || f <= 0)
        { Say("Ange sträckor på minst 50 mm och en hastighet större än 0.", Bad); return; }
        // The whole triangle must fit before the first move, so the Y move cannot be stopped by a limit halfway.
        if (SquaringCheck.Fits(Sx, Sy, dx, dy, (x, y) => _checkJog(x, y, 0)) is string problem) { Say("Triangeln får inte plats: " + problem, Bad); return; }
        if (await Jog(Sx * dx, 0, f)) { _at = 1; _moved = null; Say("Märk punkt 2.", Good); }
        UpdateButtons();
    }

    private async Task MoveY()
    {
        if (!P(_distX, out var dx) || !P(_distY, out var dy) || dy < 50 || !P(_feed, out var f) || f <= 0) { Say("Ange sträcka och hastighet.", Bad); return; }
        if (_checkJog(0, Sy * dy, 0) is string problem) { Say(problem, Bad); return; }
        if (await Jog(0, Sy * dy, f))
        {
            _at = 2; _moved = (Sx * dx, Sy * dy);
            Say("Märk punkt 3 och mät avstånden.", Good);
            _a.Focus();
        }
        UpdateButtons();
    }

    /// <summary>Back the same way: Y first, then X.</summary>
    private async Task MoveBack()
    {
        if (!P(_distX, out var dx) || !P(_distY, out var dy) || !P(_feed, out var f) || f <= 0) { Say("Ange sträckor och hastighet.", Bad); return; }
        if (_at == 2 && !await Jog(0, -Sy * dy, f)) { UpdateButtons(); return; }
        if (_at == 2) _at = 1;
        if (_at == 1 && await Jog(-Sx * dx, 0, f)) { _at = 0; Say("Tillbaka vid punkt 1. Du kan göra om kontrollen, till exempel efter en justering.", Soft); }
        UpdateButtons();
    }

    /// <summary>One $J= jog, then waits until the machine is Idle again. False (with the reason shown) if it did not go.</summary>
    private async Task<bool> Jog(double dx, double dy, double feed)
    {
        _busy = true; UpdateButtons();
        try
        {
            var r = await _grbl.SendLineAsync(GrblMachineCommands.Jog(dx, dy, 0, feed));
            if (!r.IsOk) { Say("Maskinen vägrade: " + r.Describe(), Bad); return false; }
            Say("Flyttar …", Soft);
            for (int i = 0; i < 900; i++)                      // wait until the move is done (max 3 min)
            {
                await Task.Delay(200);
                var st = await _grbl.RequestStatusAsync(TimeSpan.FromSeconds(1));
                if (st?.State == "Idle") return true;
                if (st?.State is "Alarm" or "Door" or "Hold:0" or "Hold:1") { Say($"Flytten stoppades (läge {st.State}). Börja om från punkt 1.", Bad); return false; }
            }
            Say("Maskinen blev inte klar med flytten.", Bad);
            return false;
        }
        catch (Exception ex) { Say("Flytten avbröts: " + ex.Message, Bad); return false; }
        finally { _busy = false; }
    }

    private void Compute()
    {
        if (!P(_distX, out var dx) || !P(_distY, out var dy)) { Say("Ange sträckorna.", Bad); return; }
        if (!P(_a, out var a) || !P(_b, out var b) || !P(_c, out var c) || !P(_span, out var span)) { Say("Ange alla tre avstånden och avståndet mellan Y-skenorna i mm.", Bad); return; }
        if (_moved is null) Say("Obs: räkningen förutsätter att punkterna märktes efter flyttarna ovan, med de valda sträckorna och riktningarna.", Warn);
        var (mx, my) = _moved ?? (Sx * dx, Sy * dy);
        var input = new SquaringInput(mx, my, a, b, c, span);
        if (SquaringCheck.Validate(input) is string error) { Say(error, Bad); return; }
        var r = SquaringCheck.Compute(input);
        var previous = _log.Entries.LastOrDefault();
        _result.Text = SquaringCheck.Describe(r) +
            (previous is null ? "" : $"\nFörra kontrollen ({previous.Time:yyyy-MM-dd HH:mm}): {Deg(previous.AngleErrorDeg)}, nu {Deg(r.AngleErrorDeg)}.");
        _result.Foreground = r.Verdict switch { SquaringVerdict.Square => Good, SquaringVerdict.Slight => Warn, _ => Bad };
        try
        {
            if (_log.Add(r, DateTime.Now, AppStorage.ComputerName)) { _log.Save(_logPath); Say("Resultatet är sparat för maskinen.", Soft); }
            else _log.Save(_logPath);
        }
        catch (Exception ex) { Say("Resultatet kunde inte sparas: " + ex.Message, Warn); }
        ShowHistory();
    }

    private void ShowHistory()
    {
        _history.Children.Clear();
        if (_log.Entries.Count == 0) { _history.Children.Add(new TextBlock { Text = "Inga ännu.", Foreground = Muted }); return; }
        foreach (var e in _log.Entries.AsEnumerable().Reverse().Take(6))
        {
            string where = Math.Abs(e.AngleErrorDeg) < SquaringCheck.SquareDeg ? "vinkelrät"
                : $"högra änden {Math.Abs(e.GantryOffsetMm).ToString("0.0", CultureInfo.InvariantCulture)} mm för långt {(e.GantryOffsetMm > 0 ? "bak" : "fram")}";
            _history.Children.Add(new TextBlock
            {
                Text = $"{e.Time:yyyy-MM-dd HH:mm}   {Deg(e.AngleErrorDeg),8}   {where}   (triangel {F(Math.Abs(e.MoveX))} × {F(Math.Abs(e.MoveY))} mm, {e.Computer})",
                Foreground = Soft, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, Margin = new Thickness(0, 1, 0, 1)
            });
        }
    }

    /// <summary>The triangle as seen from the front (Y+ = up on the screen), with the points numbered and the current one marked.</summary>
    private void DrawSketch()
    {
        _sketch.Children.Clear();
        double w = 110, h = 80;
        double x1 = Sx > 0 ? 25 : 25 + w, y1 = Sy > 0 ? 25 + h : 25;
        var p = new[] { new Point(x1, y1), new Point(x1 + Sx * w, y1), new Point(x1 + Sx * w, y1 - Sy * h) };
        void Seg(Point a, Point b, bool dashed) => _sketch.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = dashed ? Muted : Line, StrokeThickness = 2,
            StrokeDashArray = dashed ? new DoubleCollection { 4, 3 } : null
        });
        Seg(p[0], p[1], false); Seg(p[1], p[2], false); Seg(p[0], p[2], true);
        for (int i = 0; i < 3; i++)
        {
            bool here = i == _at;
            var dot = new Ellipse { Width = here ? 14 : 10, Height = here ? 14 : 10, Fill = here ? B(0xFF, 0x5C, 0x6C) : Ink };
            Canvas.SetLeft(dot, p[i].X - dot.Width / 2); Canvas.SetTop(dot, p[i].Y - dot.Height / 2);
            _sketch.Children.Add(dot);
            var label = new TextBlock { Text = (i + 1).ToString(), Foreground = Ink, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(label, p[i].X + (i == 0 ? -Sx * 16 : Sx * 8) - 4); Canvas.SetTop(label, p[i].Y + (i == 2 ? -Sy * 4 - 10 : Sy * 4 - 2));
            _sketch.Children.Add(label);
        }
        var back = new TextBlock { Text = "↑ bak (Y+)", Foreground = Muted, FontSize = 10 };
        Canvas.SetLeft(back, 0); Canvas.SetTop(back, 0);
        _sketch.Children.Add(back);
    }

    private static string Deg(double d) => (d >= 0 ? "+" : "−") + Math.Abs(d).ToString("0.00", CultureInfo.InvariantCulture) + "°";
    private void Say(string text, Brush brush) { _status.Text = text; _status.Foreground = brush; }
    private static bool P(TextBox t, out double v) =>
        double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static TextBox Box() => new() { Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 2, 0, 0) };
    private static TextBlock Label(string t) => new() { Text = t, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 0, 0, 6) };
    private static Border Card1(UIElement child) => new() { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10), Child = child };

    private static Grid Row(params (string Label, Control Box)[] cells)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var _ in cells) g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < cells.Length; i++)
        {
            var s = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0), Children = { new TextBlock { Text = cells[i].Label, Foreground = Muted, FontSize = 11 }, cells[i].Box } };
            Grid.SetColumn(s, i); g.Children.Add(s);
        }
        return g;
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
