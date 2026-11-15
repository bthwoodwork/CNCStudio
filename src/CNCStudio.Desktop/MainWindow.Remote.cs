using System.Windows;
using System.Windows.Controls;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;
using CNCStudio.Core.Remote;

namespace CNCStudio.Desktop;

/// <summary>
/// Maskin → Styr från mobilen…: the phone's commands (see <see cref="RemoteServer"/>) run here, on the UI thread,
/// with the same rules as the buttons – but never open a dialog on the computer: a refusal goes back to the phone.
/// Starting a program is only done at the computer.
/// </summary>
public partial class MainWindow : IRemoteMachine
{
    private RemoteServer? _remote;
    private RemoteWindow? _remoteWindow;

    private void Remote_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteWindow is { IsLoaded: true }) { _remoteWindow.Activate(); return; }
        _remoteWindow = new RemoteWindow(this) { Owner = this };
        _remoteWindow.Show();
    }

    internal RemoteServer? RemoteServer => _remote;

    internal string? StartRemote(int port)
    {
        if (_remote is not null) return null;
        var s = new RemoteServer(this);
        try { s.Start(port); }
        catch (Exception ex) { return $"Porten {port} kunde inte öppnas: {ex.Message}"; }
        s.CommandReceived += c => Dispatcher.BeginInvoke(() => { StatusText.Text = "Mobilen: " + c; _remoteWindow?.Log(c); });
        _remote = s;
        RemoteIndicator.Visibility = Visibility.Visible;
        return null;
    }

    internal void StopRemote()
    {
        _remote?.Stop();
        _remote = null;
        RemoteIndicator.Visibility = Visibility.Collapsed;
    }

    // ---- IRemoteMachine (called on worker threads: everything goes through the dispatcher) ----

    Task<RemoteStatus> IRemoteMachine.StatusAsync() => Dispatcher.InvokeAsync(BuildRemoteStatus).Task;

    Task<string?> IRemoteMachine.CommandAsync(string action, IReadOnlyDictionary<string, string> args) =>
        Dispatcher.InvokeAsync(() => RemoteCommand(action, args)).Task.Unwrap();

    private RemoteStatus BuildRemoteStatus()
    {
        var g = Grbl;
        var st = g?.LastStatus;
        double[]? work = st?.WPos ?? (st?.MPos is { Length: >= 3 } m && _lastWco is { Length: >= 3 } w ? new[] { m[0] - w[0], m[1] - w[1], m[2] - w[2] } : null);
        double[]? machine = CurrentMachinePosition();
        var runner = _runner;
        string speed = PresetPrecise.IsChecked == true ? "precise" : PresetRapid.IsChecked == true ? "rapid" : "normal";
        return new RemoteStatus(g is not null, st?.State ?? (g is null ? "Ej ansluten" : "–"), work, machine, _homedThisSession,
            runner is not null ? _jobName : _jobLines is not null ? _jobName + " (laddat)" : null,
            runner is not null && runner.TotalLines > 0 ? (double)runner.AckedLines / runner.TotalLines : null,
            JobProgressText.Text, st?.Overrides ?? new double[] { 100, 100, 100 },
            _spindleOn, _floodOn, _locations.Locations.Select(l => l.Name).ToList(), speed, false, JogStatusText.Text);
    }

    private async Task<string?> RemoteCommand(string action, IReadOnlyDictionary<string, string> args)
    {
        if (Grbl is not GrblClient g) return "Maskinen är inte ansluten till CNC Studio.";
        string state = (g.LastStatus?.State ?? "").Split(':')[0];
        string Arg(string k) => args.TryGetValue(k, out var v) ? v : "";
        string? NeedIdle(string what) => _runner is not null || _macroRunner is not null ? "Ett program körs – " + what + " går inte nu."
            : GrblMachineCommands.StateAllowsGCode(g.LastStatus?.State) ? null
            : state == "Alarm" ? "Maskinen är i larm – kör homing eller lås upp först." : $"{what} går bara när maskinen står still (nu: {state}).";
        async Task<string?> Lines(IEnumerable<string> lines)
        {
            foreach (var l in lines)
            {
                var r = await g.SendLineAsync(l);
                if (!r.IsOk) return $"{l}: {r.Describe()}";
            }
            return null;
        }
        switch (action)
        {
            case "stop":
                // The phone's STOPP is a feed hold (and jog cancel): the machine stops at once, nothing is lost.
                _jogHeld = false; CancelJog(); g.SendRealtime(GrblProtocol.FeedHold);
                JogStatusText.Text = "STOPP från mobilen – pausat. Fortsätt eller stoppa programmet.";
                return null;
            case "pause": g.SendRealtime(GrblProtocol.FeedHold); return null;
            case "resume": g.SendRealtime(GrblProtocol.CycleStartResume); return null;
            case "jogstop": _jogHeld = false; CancelJog(); return null;
            case "jog":
            {
                string dir = Arg("dir");
                if (GrblMachineCommands.JogDirection(dir) is null) return "Okänd riktning.";
                if (_runner is not null || _macroRunner is not null) return "Ett program körs – joggen är avstängd.";
                if (!GrblMachineCommands.StateAllowsJog(g.LastStatus?.State)) return $"Joggen går inte nu (maskinen: {state}).";
                await StepJogAsync(dir);
                return null;
            }
            case "speed":
                (Arg("to") switch { "precise" => PresetPrecise, "rapid" => PresetRapid, _ => PresetNormal }).IsChecked = true;
                return null;
            case "home":
            {
                if (_runner is not null || _macroRunner is not null) return "Ett program körs.";
                if (state is not ("Idle" or "Alarm")) return $"Homing går inte nu (maskinen: {state}).";
                JogStatusText.Text = "Homing från mobilen …";
                var r = await g.SendLineAsync("$H", timeout: TimeSpan.FromMinutes(2));
                if (!r.IsOk) return "Homing misslyckades: " + r.Describe();
                _homedThisSession = true;
                JogStatusText.Text = "Homing klar.";
                return null;
            }
            case "unlock": return state == "Alarm" ? await Lines(new[] { "$X" }) : null;
            case "zero":
            {
                if (NeedIdle("Nollning") is string why) return why;
                string axes = Arg("axes");
                if (axes is not ("XY" or "Z")) return "Okänd axel.";
                var err = await Lines(new[] { GrblMachineCommands.Zero(axes.Contains('X'), axes.Contains('Y'), axes.Contains('Z')) });
                if (err is null && axes == "Z") UpdateStockTopFromMachine(g, 0);
                if (err is null) JogStatusText.Text = $"Nollat {axes} från mobilen.";
                return err;
            }
            case "goto0":
                if (NeedIdle("Gå till X0 Y0") is string w0) return w0;
                if (!_homedThisSession) return "Kör homing först.";
                return await Lines(GrblMachineCommands.GoToXY(0, 0, _document.Machine));
            case "location":
            {
                if (NeedIdle("Gå till platsen") is string wl) return wl;
                if (!_homedThisSession) return "Kör homing först.";
                var loc = _locations.Locations.FirstOrDefault(l => l.Name == Arg("name"));
                if (loc is null) return "Platsen finns inte.";
                try { return await Lines(SavedLocations.GoTo(loc, JogLimits)); }
                catch (ArgumentException ex) { return ex.Message; }
            }
            case "feed":
                g.SendRealtime(Arg("by") switch { "+10" => GrblProtocol.FeedOverrideCoarsePlus, "-10" => GrblProtocol.FeedOverrideCoarseMinus, _ => GrblProtocol.FeedOverrideReset });
                return null;
            case "spindleov":
                g.SendRealtime(Arg("by") switch { "+10" => GrblProtocol.SpindleOverrideCoarsePlus, "-10" => GrblProtocol.SpindleOverrideCoarseMinus, _ => GrblProtocol.SpindleOverrideReset });
                return null;
            case "rapid":
                g.SendRealtime(Arg("to") switch { "25" => GrblProtocol.RapidOverrideLow, "50" => GrblProtocol.RapidOverrideMedium, _ => GrblProtocol.RapidOverrideReset });
                return null;
            case "flood":
            {
                var c = SpindleCoolant.Coolant(g.LastStatus, mist: false);
                if (c.Refused is string why) return why;
                g.SendRealtime(c.Realtime!.Value);
                return null;
            }
            case "spindle":
                return "Spindeln startas och stoppas bara vid datorn.";
            case "start":
                return "Program startas bara vid datorn (kontrollerna där kräver att du ser maskinen).";
        }
        return "Okänt kommando.";
    }
}

/// <summary>The computer's side: the address and PIN to open on the phone, start/stop, and what the phone has done.</summary>
public sealed class RemoteWindow : Window
{
    private readonly MainWindow _main;
    private readonly TextBlock _urls = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.White };
    private readonly TextBlock _pin = new() { FontSize = 34, FontWeight = FontWeights.Bold, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Foreground = System.Windows.Media.Brushes.White };
    private readonly TextBox _port = new() { Text = RemoteServer.DefaultPort.ToString(), Width = 80, Padding = new Thickness(4, 2, 4, 2) };
    private readonly Button _toggle = new() { FontWeight = FontWeights.SemiBold, Padding = new Thickness(12, 6, 12, 6) };
    private readonly ListBox _log = new() { Height = 150, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0x20, 0x2E)), Foreground = System.Windows.Media.Brushes.White };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Salmon, TextWrapping = TextWrapping.Wrap };

    public RemoteWindow(MainWindow main)
    {
        _main = main;
        Title = "Styr från mobilen";
        Width = 560; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x17, 0x21, 0x2C));
        Foreground = System.Windows.Media.Brushes.White;
        var muted = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8C, 0xA3, 0xBA));
        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock
        {
            Text = "Telefonen måste vara på samma nätverk (wifi) som datorn. Öppna adressen i telefonens webbläsare och skriv PIN-koden. " +
                   "Där finns jogg, STOPP, paus/fortsätt, nollning, homing, överstyrning och sparade platser. Program startas bara vid datorn.\n\n" +
                   "Windows frågar första gången om CNC Studio får ta emot anslutningar – tillåt för privata nätverk.",
            Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        body.Children.Add(new WrapPanel { Children = { new Label { Content = "Port", Foreground = muted }, _port, new Border { Width = 12 }, _toggle } });
        body.Children.Add(new TextBlock { Text = "ADRESS", Foreground = muted, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 2) });
        body.Children.Add(_urls);
        body.Children.Add(new TextBlock { Text = "PIN", Foreground = muted, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 2) });
        body.Children.Add(_pin);
        body.Children.Add(_error);
        body.Children.Add(new TextBlock { Text = "FRÅN MOBILEN", Foreground = muted, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 2) });
        body.Children.Add(_log);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _toggle.Click += (_, _) =>
        {
            _error.Text = "";
            if (_main.RemoteServer is not null) _main.StopRemote();
            else if (!int.TryParse(_port.Text.Trim(), out var p) || p < 1024 || p > 65535) _error.Text = "Porten måste vara 1024–65535.";
            else if (_main.StartRemote(p) is string err) _error.Text = err;
            Show();
        };
        Show();
    }

    private new void Show()
    {
        var s = _main.RemoteServer;
        _toggle.Content = s is null ? "Starta" : "Stoppa";
        _port.IsEnabled = s is null;
        if (s is null) { _urls.Text = "– (inte startad)"; _pin.Text = "––––––"; return; }
        var urls = RemoteServer.LocalUrls(s.Port);
        _urls.Text = urls.Count == 0 ? $"http://<datorns IP>:{s.Port}/ (ingen nätverksadress hittades)" : string.Join("\n", urls);
        _pin.Text = s.Pin;
        if (!IsVisible) base.Show();
    }

    public void Log(string text)
    {
        _log.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (_log.Items.Count > 200) _log.Items.RemoveAt(_log.Items.Count - 1);
    }
}
