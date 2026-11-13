using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Machine table coordinates, homing, the live tool position and setting the work area.
/// <para>
/// Table coordinates are measured from the corner where the X/Y switches sit (MachineProfile.HomeCorner,
/// from $23): the user picks that corner in Maskininställningar and positions are counted from it.
/// The canvas itself keeps its own coordinates (origin top-left = back-left, Y down).
/// </para>
/// <para>
/// Verified (limits.c, system.c, settings.c): machine space is always −$13x…0 whatever the homing
/// direction; an axis homed toward plus stands at −$27 afterwards, one homed toward minus at
/// −$13x + $27. With +X to the right and +Y away from the operator (checked with $3) the tool is at
/// canvas X = $130 + MPos X and canvas Y = −MPos Y.
/// </para>
/// </summary>
public partial class MainWindow
{
    private bool _homedThisSession;
    private Canvas? _toolMarker;

    private bool OriginLeft => _document.Machine.HomeCorner is HomeCorner.BackLeft or HomeCorner.FrontLeft;
    private bool OriginFront => _document.Machine.HomeCorner is HomeCorner.FrontLeft or HomeCorner.FrontRight;

    /// <summary>Canvas rectangle (top-left x, y, size w × h) → distance of its nearest corner from the origin corner.</summary>
    private (double X, double Y) ToTable(double cx, double cy, double w, double h) =>
        (OriginLeft ? cx : _document.Machine.WorkWidthMm - (cx + w),
         OriginFront ? _document.Machine.WorkLengthMm - (cy + h) : cy);

    /// <summary>Inverse of <see cref="ToTable"/>.</summary>
    private (double X, double Y) FromTable(double tx, double ty, double w, double h) =>
        (OriginLeft ? tx : _document.Machine.WorkWidthMm - tx - w,
         OriginFront ? _document.Machine.WorkLengthMm - ty - h : ty);

    private void ShowStockFields()
    {
        var (x, y) = ToTable(_document.Stock.X, _document.Stock.Y, _document.MaterialWidth, _document.MaterialHeight);
        StockXBox.Text = F(x); StockYBox.Text = F(y);
    }

    private static string CornerName(HomeCorner c) => c switch
    {
        HomeCorner.BackLeft => "bak vänster", HomeCorner.BackRight => "bak höger",
        HomeCorner.FrontLeft => "fram vänster", _ => "fram höger"
    };

    // ---- Homing ----

    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        if (Grbl is not GrblClient g) { Error("Anslut till maskinen först."); return; }
        if (AppDialog.Show(this, "Kör homing ($H)?\n\nMaskinen rör sig: först Z uppåt mot brytaren, sedan X och Y mot sina brytare " +
                $"({CornerName(_document.Machine.HomeCorner)}). Kontrollera att inget är i vägen och att fräsen inte står i materialet.",
                "Homing", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        HomeButton.IsEnabled = false;
        JogStatusText.Text = "Homing pågår …";
        try
        {
            var r = await g.SendLineAsync("$H", timeout: TimeSpan.FromMinutes(2)); // $H answers when finished
            if (r.IsOk)
            {
                _homedThisSession = true;
                JogStatusText.Text = "Homing klar – maskinkoordinaterna är kända.";
                await ReadMachineSettingsAsync(g, offerProfileUpdate: false);   // $130/$131 may have changed since connecting
                if (_machineView) RenderMachine(refreshCam: false);
            }
            else JogStatusText.Text = "Homing misslyckades: " + r.Describe();
        }
        catch (Exception ex) { JogStatusText.Text = "Homing avbröts: " + ex.Message; }
        finally { HomeButton.IsEnabled = true; }
    }

    // ---- Live tool position in the machine view ----

    private void ShowTool_Changed(object sender, RoutedEventArgs e) { if (_machineView) RenderMachine(refreshCam: false); }

    /// <summary>Called from RenderMachine: (re)creates the marker at the current zoom.</summary>
    private void AddToolMarker()
    {
        _toolMarker = null;
        if (ShowToolBox?.IsChecked != true || !_homedThisSession || Grbl is null) return;
        double s = 1 / _zoom;
        var c = new Canvas { IsHitTestVisible = false };
        var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));
        c.Children.Add(new Ellipse { Width = 26 * s, Height = 26 * s, Fill = new SolidColorBrush(Color.FromArgb(50, 0xFF, 0x5A, 0x5A)),
            Stroke = red, StrokeThickness = 1.5 * s, Margin = new Thickness(-13 * s, -13 * s, 0, 0) });
        c.Children.Add(new Line { X1 = -20 * s, X2 = 20 * s, Stroke = red, StrokeThickness = 1 * s });
        c.Children.Add(new Line { Y1 = -20 * s, Y2 = 20 * s, Stroke = red, StrokeThickness = 1 * s });
        c.Children.Add(new Ellipse { Width = 4 * s, Height = 4 * s, Fill = Brushes.White, Margin = new Thickness(-2 * s, -2 * s, 0, 0) });
        var label = new TextBlock { FontSize = 10.5 * s, Foreground = red, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14 * s, 10 * s, 0, 0) };
        c.Children.Add(label);
        c.Tag = label;
        Panel.SetZIndex(c, 100);
        MachineCanvas.Children.Add(c);
        _toolMarker = c;
        UpdateToolMarker();
    }

    /// <summary>
    /// Where the tool is on the machine table: (x, y) on the table canvas (from the back-left corner, Y to
    /// the front) and (tx, ty) from the home corner, as the marker's label shows.
    /// </summary>
    private (double X, double Y, double Tx, double Ty) ToolOnTable(double[] mpos)
    {
        double tx, ty, x, y;
        if (_grblSettings is { } s)
        {
            // From the machine's own $23/$130/$131: right even when the project's profile is another size.
            (tx, ty) = Commissioning.DistanceFromSwitches(mpos, s);
            (x, y) = FromTable(tx, ty, 0, 0);
        }
        else
        {
            x = _document.Machine.WorkWidthMm + mpos[0]; y = -mpos[1];
            (tx, ty) = ToTable(x, y, 0, 0);
        }
        return (x, y, tx, ty);
    }

    /// <summary>
    /// The tool in the 3D view, on every status report: shown on the same conditions as the 2D marker
    /// (connected, homed, not in alarm). The height is the work Z (Z0 = the material's top) when it is
    /// known; otherwise only the position on the table is shown.
    /// </summary>
    private void Update3DTool()
    {
        if (!_show3D || View3DPanel is null) return;
        var st = _grbl?.LastStatus;
        if (!_homedThisSession || Grbl is null || st?.MPos is not { Length: >= 3 } mpos || st.State == "Alarm") { View3DPanel.ShowTool(null); return; }
        var (x, y, _, _) = ToolOnTable(mpos);
        var (vx, vy) = MachineTable3D.FromTable(MachineTable3D.For(_document), x, y);
        double? z = st.WPos is { Length: >= 3 } w ? w[2] : _lastWco is { Length: >= 3 } wco ? mpos[2] - wco[2] : null;
        View3DPanel.ShowTool(new View3DPanel.ToolPosition(vx, vy, z));
    }

    /// <summary>Moves the marker on each status report – no redraw of the view.</summary>
    private void UpdateToolMarker()
    {
        if (_toolMarker is null) return;
        var mpos = _grbl?.LastStatus?.MPos;
        if (mpos is not { Length: >= 3 } || _grbl?.LastStatus?.State == "Alarm") { _toolMarker.Visibility = Visibility.Collapsed; return; }
        _toolMarker.Visibility = Visibility.Visible;
        var (x, y, tx, ty) = ToolOnTable(mpos);
        Canvas.SetLeft(_toolMarker, x); Canvas.SetTop(_toolMarker, y);
        if (_toolMarker.Tag is TextBlock label)
            label.Text = $"X {tx.ToString("0.0", CultureInfo.InvariantCulture)}  Y {ty.ToString("0.0", CultureInfo.InvariantCulture)}";
    }

    // ---- Work area (soft limits) from the current position ----

    /// <summary>
    /// After homing: the distance from each X/Y switch to where the tool stands now becomes $130/$131.
    /// GRBL's soft limits then keep every move inside that area. Written with verified read-back.
    /// </summary>
    private async void SetTravelHere_Click(object sender, RoutedEventArgs e)
    {
        if (Grbl is not GrblClient g) { Error("Anslut till maskinen först."); return; }
        if (!_homedThisSession) { Error("Kör homing först – annars vet maskinen inte var brytarna är."); return; }
        try
        {
            var status = await g.RequestStatusAsync(TimeSpan.FromSeconds(2));
            if (status?.State != "Idle" || status.MPos is not { Length: >= 3 } mpos)
            { Error($"Maskinen måste stå still (Idle). Nu: {status?.State ?? "okänt"}."); return; }
            var settings = await g.ReadSettingsAsync();
            double Get(int n) => settings.TryGetValue(n, out var v) ? v : 0;
            int homeMask = (int)Get(23);
            double pull = Get(27), margin = _document.Machine.LimitMarginMm;
            var changes = new List<(int, double)>();
            var lines = new List<string>();
            bool rehome = false;
            for (int i = 0; i < 2; i++)
            {
                double travel = Get(130 + i);
                bool homesMinus = (homeMask & (1 << i)) != 0;
                // Distance from the switch end: plus-homed switch at MPos 0, minus-homed at −$13x.
                double d = Math.Floor((homesMinus ? mpos[i] + travel : -mpos[i]) * 10) / 10;
                string axis = i == 0 ? "X" : "Y";
                if (d < pull + margin + 10)
                { Error($"{axis}: fräsen står bara {F(d)} mm från brytaren. Jogga ut till den yttersta punkten maskinen får nå först."); return; }
                if (Math.Abs(d - travel) < 0.05) continue;
                changes.Add((130 + i, d));
                lines.Add($"  ${130 + i} ({axis}-rörelse): {F(travel)} → {F(d)} mm");
                rehome |= homesMinus;
            }
            if (Get(20) != 1 && Get(22) == 1) { changes.Add((20, 1)); lines.Add("  $20 (mjuka gränser): 0 → 1"); }
            if (changes.Count == 0) { JogStatusText.Text = "Arbetsområdet är redan satt till den här punkten."; return; }
            if (AppDialog.Show(this,
                    "Sätta maskinens arbetsområde till den punkt fräsen står på nu?\n\n" + string.Join("\n", lines) +
                    "\n\nVärdena skrivs till maskinens EEPROM och kontrolläses. GRBL:s mjuka gränser håller sedan alla rörelser inom området." +
                    (rehome ? "\n\nEn axel homar åt minus – kör homing igen efteråt så att koordinaterna stämmer med det nya området." : ""),
                    "Arbetsområde", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            var result = await g.WriteSettingsAsync(changes);
            if (!result.Success) { Error(result.Message); return; }
            RememberChange();
            foreach (var (n, v) in changes)
            {
                if (n == 130) _document.Machine.WorkWidthMm = v;
                if (n == 131) _document.Machine.WorkLengthMm = v;
                if (n == 20) _document.Machine.SoftLimits = true;
            }
            if (rehome) _homedThisSession = false;
            Render();
            JogStatusText.Text = result.Message + (rehome ? " Kör homing igen." : "");
        }
        catch (Exception ex) { Error("Kunde inte sätta arbetsområdet: " + ex.Message); }
    }
}
