using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Real coordinates, zeroing, go-to and jogging. All commands come from GrblMachineCommands
/// (verified against GRBL 1.1) and are only sent in states GRBL accepts.
/// </summary>
public partial class MainWindow
{
    /// <summary>Hold longer than this to jog continuously (gSender uses 250 ms).</summary>
    private static readonly TimeSpan ContinuousJogDelay = TimeSpan.FromMilliseconds(250);

    private DispatcherTimer? _jogHoldTimer;
    private bool _jogHeld, _jogContinuous;
    private Task? _jogLoop;
    private bool _syncingWcs;
    private string? _activeWcs;

    private void SetMachineControlsEnabled(bool connected)
    {
        if (MachineControls is null) return;
        MachineControls.IsEnabled = connected;
        MachineControlsHint.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (!connected) { DroX.Text = DroY.Text = DroZ.Text = "–"; MposX.Text = MposY.Text = MposZ.Text = ""; JogStatusText.Text = ""; }
    }

    /// <summary>Called for every status report: work coordinates large, machine coordinates small.</summary>
    private void ShowDro(double[]? mpos, double[]? wpos)
    {
        string F3(double[]? v, int i) => v is { Length: > 2 } ? v[i].ToString("0.000", CultureInfo.InvariantCulture) : "–";
        DroX.Text = F3(wpos, 0); DroY.Text = F3(wpos, 1); DroZ.Text = F3(wpos, 2);
        MposX.Text = mpos is null ? "" : "MPos " + F3(mpos, 0);
        MposY.Text = mpos is null ? "" : "MPos " + F3(mpos, 1);
        MposZ.Text = mpos is null ? "" : "MPos " + F3(mpos, 2);
    }

    private GrblClient? RequireIdle(string action)
    {
        if (Grbl is not GrblClient g) { Error("Inte ansluten till maskinen."); return null; }
        var state = g.LastStatus?.State;
        if (!GrblMachineCommands.StateAllowsGCode(state))
        {
            JogStatusText.Text = state == "Alarm"
                ? $"{action}: maskinen är i larm – kör homing ($H) i konsolen först."
                : $"{action} går bara när maskinen är Idle (nu: {state ?? "okänt"}).";
            return null;
        }
        return g;
    }

    private async Task<bool> SendAsync(GrblClient g, string line)
    {
        try
        {
            var r = await g.SendLineAsync(line);
            if (r.IsOk) return true;
            JogStatusText.Text = $"{line}: {r.Describe()}";
        }
        catch (Exception ex) { JogStatusText.Text = ex.Message; }
        return false;
    }

    // ---- Zero, set value, go to ----

    private async void Zero_Click(object sender, RoutedEventArgs e)
    {
        var axes = ((FrameworkElement)sender).Tag?.ToString() ?? "";
        if (RequireIdle("Nollning") is not GrblClient g) return;
        var line = GrblMachineCommands.Zero(axes.Contains('X'), axes.Contains('Y'), axes.Contains('Z'));
        if (!await SendAsync(g, line)) return;
        JogStatusText.Text = $"Nollat {string.Join(" + ", axes.ToCharArray())} i {_activeWcs ?? "aktivt koordinatsystem"}.";
        if (axes.Contains('Z')) UpdateStockTopFromMachine(g, 0);
    }

    private async void SetWork_Click(object sender, RoutedEventArgs e)
    {
        if (!Parse(SetPositionBox, out var value)) { Error("Ogiltigt värde. Skriv ett tal i mm.", SetPositionBox); return; }
        if (RequireIdle("Sätt värde") is not GrblClient g) return;
        char axis = ((AxisPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "X")[0];
        var line = GrblMachineCommands.SetWork(axis == 'X' ? value : null, axis == 'Y' ? value : null, axis == 'Z' ? value : null);
        if (!await SendAsync(g, line)) return;
        JogStatusText.Text = $"{axis} är nu {value.ToString("0.###", CultureInfo.InvariantCulture)} i {_activeWcs ?? "aktivt koordinatsystem"}.";
        if (axis == 'Z') UpdateStockTopFromMachine(g, value);
    }

    /// <summary>
    /// After setting work Z, the stock top (work Z0) is at MPos Z − value. Store it as "MPos Z vid
    /// materialytan" so the Z-limit checks use the real setup. Only after homing (known MPos).
    /// </summary>
    private void UpdateStockTopFromMachine(GrblClient g, double workZ)
    {
        if (!_document.Machine.HomeOnStartup || g.LastStatus?.MPos is not { Length: > 2 } mpos) return;
        double stockTop = mpos[2] - workZ;
        if (stockTop >= 0) return;
        RememberChange();
        _document.Motion.StockTopMachineZ = Math.Round(stockTop, 3);
        RefreshMotionFields();
        Render();
        StatusText.Text = $"MPos Z vid materialytan uppdaterad till {stockTop.ToString("0.###", CultureInfo.InvariantCulture)} – Z-kontrollerna använder nu den.";
        OnZeroChanged();
    }

    private ProbeWindow? _probeWindow;

    /// <summary>Opens probing (Z by default). Needs homing: corner probing moves in machine coordinates (G53).</summary>
    private void Probe_Click(object sender, RoutedEventArgs e)
    {
        if (RequireIdle("Probning") is not GrblClient g) return;
        if (!_homedThisSession) { Error("Kör homing först (⌂ Homing). Probningen flyttar i maskinkoordinater och behöver veta var maskinen står."); return; }
        if (_probeWindow is { IsLoaded: true }) { _probeWindow.Activate(); return; }
        double tool = _loadedJob?.ToolDiameter ?? SelectedTool?.Diameter
                      ?? _document.CamOperations.Select(o => (double?)o.ToolDiameter).FirstOrDefault() ?? 6;
        _probeWindow = new ProbeWindow(g, AppStorage.Current.LoadProbeSettings(), tool, _document.WorkZero == WorkZero.Center,
            r => Dispatcher.Invoke(() => OnProbed(r))) { Owner = this };
        _probeWindow.Show();
    }

    /// <summary>After probing: Z probed → the stock top (MPos Z) is known, so the Z checks and a waiting program use it.</summary>
    private void OnProbed(ProbeResult r)
    {
        JogStatusText.Text = r.Message;
        if (r.StockTopMPosZ is not double z || z >= 0) return;
        RememberChange();
        _document.Motion.StockTopMachineZ = Math.Round(z, 3);
        RefreshMotionFields();
        Render();
        StatusText.Text = $"Probat: MPos Z vid materialytan {z.ToString("0.###", CultureInfo.InvariantCulture)} – Z-kontrollerna använder nu den.";
        OnZeroChanged();
    }
    private async void GoTo_Click(object sender, RoutedEventArgs e)
    {
        var axes = ((FrameworkElement)sender).Tag?.ToString() ?? "";
        if (RequireIdle("Gå till") is not GrblClient g) return;
        var lines = GrblMachineCommands.GoToXY(axes.Contains('X') ? 0 : null, axes.Contains('Y') ? 0 : null, _document.Machine);
        foreach (var line in lines)
            if (!await SendAsync(g, line)) return;
        JogStatusText.Text = $"Går till {(axes == "XY" ? "X0 Y0" : axes + "0")} (Z lyftes först).";
    }

    // ---- Work coordinate system ----

    private async Task ReadActiveWcsAsync(GrblClient g)
    {
        try
        {
            var r = await g.SendLineAsync("$G");
            var wcs = r.Lines.Select(l => GrblMachineCommands.ParseActiveWcs(l.Raw)).FirstOrDefault(w => w is not null);
            if (wcs is null) return;
            _activeWcs = wcs;
            _syncingWcs = true;
            WcsPicker.SelectedIndex = int.Parse(wcs[1..], CultureInfo.InvariantCulture) - 54;
            _syncingWcs = false;
            JogStatusText.Text = wcs == "G54" ? "" :
                $"Aktivt koordinatsystem är {wcs}. CNC Studios G-code använder G54 – nolla i G54 innan du kör den.";
        }
        catch (Exception) { /* informational */ }
    }

    private async void Wcs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingWcs || WcsPicker.SelectedIndex < 0) return;
        if (RequireIdle("Byta koordinatsystem") is not GrblClient g) return;
        if (!await SendAsync(g, GrblMachineCommands.SelectWcs(54 + WcsPicker.SelectedIndex))) return;
        await ReadActiveWcsAsync(g);
    }

    // ---- Jogging ----

    private void JogPreset_Checked(object sender, RoutedEventArgs e)
    {
        if (JogStepXY is null || sender is not RadioButton { Tag: string tag }) return;
        var p = GrblMachineCommands.DefaultPresets[int.Parse(tag, CultureInfo.InvariantCulture)];
        JogStepXY.Text = F(p.StepXY); JogStepZ.Text = F(p.StepZ); JogFeed.Text = F(p.FeedRate);
    }

    private bool ReadJog(string tag, out double dx, out double dy, out double dz, out double feed, bool step)
    {
        dx = dy = dz = feed = 0;
        if (!Parse(JogStepXY, out var sxy) || !Parse(JogStepZ, out var sz) || !Parse(JogFeed, out var f) || sxy <= 0 || sz <= 0 || f <= 0)
        { JogStatusText.Text = "Ange giltiga steg och hastighet för joggen."; return false; }
        bool z = tag[0] == 'Z';
        feed = GrblMachineCommands.ClampFeed(f, _document.Machine, xy: !z, z: z);
        double s = step ? (z ? sz : sxy) : GrblMachineCommands.ContinuousStep(feed);
        double sign = tag[1] == '+' ? 1 : -1;
        switch (tag[0]) { case 'X': dx = sign * s; break; case 'Y': dy = sign * s; break; default: dz = sign * s; break; }
        return true;
    }

    private void Jog_Down(object sender, MouseButtonEventArgs e)
    {
        if (Grbl is null || sender is not Button { Tag: string tag } button) return;
        e.Handled = true;
        _jogHeld = true; _jogContinuous = false;
        button.CaptureMouse();
        _jogHoldTimer?.Stop();
        _jogHoldTimer = new DispatcherTimer { Interval = ContinuousJogDelay };
        _jogHoldTimer.Tick += (_, _) =>
        {
            _jogHoldTimer?.Stop();
            if (!_jogHeld) return;
            _jogContinuous = true;
            _jogLoop = ContinuousJogAsync(tag);
        };
        _jogHoldTimer.Start();
    }

    private async void Jog_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_jogHeld || sender is not Button { Tag: string tag } button) return;
        e.Handled = true;
        _jogHeld = false;
        _jogHoldTimer?.Stop();
        button.ReleaseMouseCapture();
        if (_jogContinuous) await StopContinuousJogAsync();
        else await StepJogAsync(tag);
    }

    private async void Jog_Lost(object sender, MouseEventArgs e)
    {
        // Focus lost while holding (e.g. a dialog opened): never keep jogging.
        if (!_jogHeld) return;
        _jogHeld = false;
        _jogHoldTimer?.Stop();
        if (_jogContinuous) await StopContinuousJogAsync();
    }

    private async Task StepJogAsync(string tag)
    {
        if (Grbl is not GrblClient g || !ReadJog(tag, out var dx, out var dy, out var dz, out var feed, step: true)) return;
        if (GrblMachineCommands.CheckJog(g.LastStatus, _document.Machine, dx, dy, dz) is string problem)
        { JogStatusText.Text = problem; return; }
        if (await SendAsync(g, GrblMachineCommands.Jog(dx, dy, dz, feed))) JogStatusText.Text = "";
    }

    /// <summary>
    /// Continuous jog as recommended in the GRBL jogging wiki: short incremental jogs, each sent only
    /// after the previous one is acknowledged ("ok"); a jog cancel (0x85) when the button is released.
    /// </summary>
    private async Task ContinuousJogAsync(string tag)
    {
        if (Grbl is not GrblClient g || !ReadJog(tag, out var dx, out var dy, out var dz, out var feed, step: false)) return;
        var start = g.LastStatus;
        double tx = 0, ty = 0, tz = 0;
        JogStatusText.Text = "";
        while (_jogHeld && Grbl is not null)
        {
            if (GrblMachineCommands.CheckJog(start, _document.Machine, tx + dx, ty + dy, tz + dz) is string problem)
            { JogStatusText.Text = problem; break; }
            if (!await SendAsync(g, GrblMachineCommands.Jog(dx, dy, dz, feed))) break;
            tx += dx; ty += dy; tz += dz;
        }
    }

    private async Task StopContinuousJogAsync()
    {
        CancelJog();                         // stop at once
        if (_jogLoop is not null) { try { await _jogLoop; } catch { } _jogLoop = null; }
        CancelJog();                         // and again, in case one more jog was sent meanwhile
    }

    private void CancelJog()
    {
        try { Grbl?.SendRealtime(GrblProtocol.JogCancel); } catch (Exception) { }
    }

    private async void JogStop_Click(object sender, RoutedEventArgs e)
    {
        _jogHeld = false;
        _jogHoldTimer?.Stop();
        await StopContinuousJogAsync();
        JogStatusText.Text = "Joggen avbröts.";
    }
}
