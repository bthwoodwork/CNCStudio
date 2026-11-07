using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Gamepad control of the machine (XInput). Sticks jog continuously with speed following how far they are
/// pushed; buttons run actions; the lockout button (default LB) must be held for anything but Stop. The
/// continuous jog follows the GRBL jogging wiki: short $J= steps, a jog cancel (0x85) the moment the stick
/// is released, the lockout is let go or the controller disconnects.
/// </summary>
public partial class MainWindow
{
    private GamepadProfile _padProfile = AppStorage.Current.LoadGamepadProfile();
    private DispatcherTimer? _padTimer;
    private GamepadState _padPrevious = GamepadState.Released;
    private bool _padConnected;
    private (double X, double Y, double Z) _padJog;
    private Task? _padJogLoop;
    private bool _padJogRunning;
    /// <summary>Each jog loop gets a number; an old loop ending never cancels a newer one.</summary>
    private int _padJogGeneration;

    private static readonly TimeSpan PadPoll = TimeSpan.FromMilliseconds(40);
    /// <summary>One continuous-jog step lasts this long at the chosen speed (wiki: 0.025–0.06 s).</summary>
    private const double PadStepSeconds = 0.06;

    private void InitGamepad()
    {
        _padTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = PadPoll };
        _padTimer.Tick += (_, _) => PollGamepad();
        _padTimer.Start();
        ShowGamepadStatus();
    }

    private void PollGamepad()
    {
        var read = _padProfile.Enabled ? XInputGamepad.Read() : null;
        if (read is null)
        {
            if (_padConnected) { _padConnected = false; StopPadJog(); ShowGamepadStatus(); }
            _padPrevious = GamepadState.Released;
            return;
        }
        if (!_padConnected) { _padConnected = true; ShowGamepadStatus(); }
        var now = read.Value.State;
        var output = GamepadMapper.Map(_padProfile, _padPrevious, now);
        _padPrevious = now;
        foreach (var a in output.Actions) RunPadAction(a);

        // Jogging only when it is safe: connected, nothing running, controller Idle or already jogging.
        bool canJog = Grbl is GrblClient g && _runner is null && GrblMachineCommands.StateAllowsJog(g.LastStatus?.State);
        if (output.Jogging && canJog)
        {
            _padJog = (output.JogX, output.JogY, output.JogZ);
            if (!_padJogRunning) { _padJogRunning = true; _padJogLoop = PadJogLoopAsync(++_padJogGeneration); }
        }
        else if (_padJogRunning) StopPadJog();
        if (GamepadLiveText is not null)
            GamepadLiveText.Text = !output.Unlocked && _padProfile.LockoutButton != GamepadButton.None
                ? $"Håll {GamepadMapper.ButtonName(_padProfile.LockoutButton)} för att styra"
                : output.Jogging ? "Joggar …" : "Redo";
    }

    /// <summary>Continuous jog: short steps along the stick direction, each sent after the previous "ok".</summary>
    private async Task PadJogLoopAsync(int generation)
    {
        if (Grbl is not GrblClient g) { _padJogRunning = false; return; }
        var start = g.LastStatus;
        double tx = 0, ty = 0, tz = 0;
        try
        {
            while (_padJogRunning && generation == _padJogGeneration && Grbl is not null)
            {
                var (jx, jy, jz) = _padJog;
                double vx = jx * _padProfile.MaxJogFeedXY, vy = jy * _padProfile.MaxJogFeedXY, vz = jz * _padProfile.MaxJogFeedZ;
                double feed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                if (feed < 1) { await Task.Delay(PadPoll); continue; }
                feed = GrblMachineCommands.ClampFeed(feed, _document.Machine, xy: vx != 0 || vy != 0, z: vz != 0);
                double scale = feed / Math.Sqrt(vx * vx + vy * vy + vz * vz);
                double dx = vx * scale / 60 * PadStepSeconds, dy = vy * scale / 60 * PadStepSeconds, dz = vz * scale / 60 * PadStepSeconds;
                if (GrblMachineCommands.CheckJog(start, JogLimits, tx + dx, ty + dy, tz + dz) is string problem)
                { JogStatusText.Text = problem; break; }
                if (!await SendAsync(g, GrblMachineCommands.Jog(Math.Round(dx, 4), Math.Round(dy, 4), Math.Round(dz, 4), feed))) break;
                tx += dx; ty += dy; tz += dz;
                // Keep GRBL's queue short so a change of direction takes effect at once.
                await Task.Delay(TimeSpan.FromSeconds(PadStepSeconds * 0.8));
            }
        }
        catch (Exception ex) { JogStatusText.Text = "Gamepad-joggen stoppades: " + ex.Message; }
        finally { if (generation == _padJogGeneration) { _padJogRunning = false; CancelJog(); } }
    }

    private void StopPadJog()
    {
        _padJog = (0, 0, 0);
        _padJogRunning = false;
        _padJogGeneration++;
        CancelJog();                                  // stop at once; the loop also cancels when it ends
    }

    private void RunPadAction(GamepadAction a)
    {
        var tag = new FrameworkElement();
        switch (a)
        {
            case GamepadAction.Stop:
                StopPadJog();
                if (_runner is not null && Grbl is GrblClient run) { run.SendRealtime(GrblProtocol.FeedHold); JogStatusText.Text = "Gamepad: programmet pausat (Fortsätt med Start)."; }
                else JogStatusText.Text = "Gamepad: stopp.";
                return;
            case GamepadAction.Pause:
                if (Grbl is GrblClient p) p.SendRealtime(GrblProtocol.FeedHold);
                return;
            case GamepadAction.Resume:
                if (Grbl is GrblClient r) r.SendRealtime(GrblProtocol.CycleStartResume);
                return;
        }
        if (Grbl is null || _runner is not null) return;           // the rest only when idle and connected
        switch (a)
        {
            case GamepadAction.JogXMinus: _ = StepJogAsync("X-"); break;
            case GamepadAction.JogXPlus: _ = StepJogAsync("X+"); break;
            case GamepadAction.JogYMinus: _ = StepJogAsync("Y-"); break;
            case GamepadAction.JogYPlus: _ = StepJogAsync("Y+"); break;
            case GamepadAction.JogZMinus: _ = StepJogAsync("Z-"); break;
            case GamepadAction.JogZPlus: _ = StepJogAsync("Z+"); break;
            case GamepadAction.NextJogSpeed:
                var next = PresetPrecise.IsChecked == true ? PresetNormal : PresetNormal.IsChecked == true ? PresetRapid : PresetPrecise;
                next.IsChecked = true;
                JogStatusText.Text = $"Jogghastighet: {next.Content}.";
                break;
            case GamepadAction.ZeroXY: tag.Tag = "XY"; Zero_Click(tag, new RoutedEventArgs()); break;
            case GamepadAction.ZeroZ: tag.Tag = "Z"; Zero_Click(tag, new RoutedEventArgs()); break;
            case GamepadAction.GoToXY0: tag.Tag = "XY"; GoTo_Click(tag, new RoutedEventArgs()); break;
            case GamepadAction.Home: Home_Click(tag, new RoutedEventArgs()); break;
            case GamepadAction.Probe: Probe_Click(tag, new RoutedEventArgs()); break;
        }
    }

    private void ShowGamepadStatus()
    {
        if (GamepadStatusText is null) return;
        GamepadStatusText.Text = !_padProfile.Enabled ? "Gamepad: avstängd"
            : _padConnected ? "Gamepad: ansluten" : "Gamepad: ingen ansluten";
        GamepadStatusText.Foreground = _padConnected && _padProfile.Enabled
            ? new SolidColorBrush(Color.FromRgb(0x7C, 0xD9, 0x9C)) : new SolidColorBrush(Color.FromRgb(0x8C, 0xA3, 0xBA));
        if (GamepadLiveText is not null && !_padConnected) GamepadLiveText.Text = "";
    }

    private void GamepadSettings_Click(object sender, RoutedEventArgs e)
    {
        var w = new GamepadWindow(_padProfile) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _padProfile = w.Result;
            try { AppStorage.Current.SaveGamepadProfile(_padProfile); }
            catch (Exception ex) { Error("Gamepad-inställningarna kunde inte sparas: " + ex.Message); }
            StopPadJog();
            ShowGamepadStatus();
        }
    }
}
