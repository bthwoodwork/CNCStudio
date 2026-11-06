using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Connection to the GRBL controller. Nothing is sent unless the user acts, except '?' status
/// requests while connected.
/// </summary>
public partial class MainWindow
{
    private GrblClient? _grbl;
    private GrblConsoleWindow? _console;
    private readonly List<string> _grblLog = new();

    internal GrblClient? Grbl => _grbl is { IsConnected: true } ? _grbl : null;
    internal IReadOnlyList<string> GrblLog => _grblLog;
    internal event Action<string>? GrblLogAdded;

    private void InitGrblUi() { RefreshPorts(); SetMachineControlsEnabled(false); }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        if (PortPicker is null) return;
        var selected = PortPicker.SelectedItem as string;
        var ports = SerialGrblTransport.AvailablePorts();
        PortPicker.ItemsSource = ports;
        PortPicker.SelectedItem = ports.Contains(selected) ? selected : ports.FirstOrDefault();
        if (_grbl is null)
            ConnectionDetailText.Text = ports.Length == 0 ? "Ingen COM-port hittades." : "Välj port och klicka Anslut.";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_grbl is not null) { DisconnectGrbl("Frånkopplad."); return; }
        if (PortPicker.SelectedItem is not string port) { Error("Välj en COM-port först."); return; }
        ConnectButton.IsEnabled = false;
        SetConnectionBanner("ANSLUTER…", $"{port}, {GrblProtocol.BaudRate} baud", Color.FromRgb(70, 80, 40));
        var client = new GrblClient(new SerialGrblTransport(port));
        client.LineReceived += line => Log("← " + GrblSettings.Describe(line));
        client.Sent += text => Log("→ " + text);
        client.StatusChanged += s => Dispatcher.BeginInvoke(() => ShowStatus(s));
        try
        {
            var hello = await client.ConnectAsync();
            _grbl = client;
            client.StartStatusPolling(TimeSpan.FromMilliseconds(250));
            SetConnectionBanner("ANSLUTEN", $"{port} · {hello}", Color.FromRgb(36, 98, 63));
            ConnectButton.Content = "Koppla från";
            Log($"Ansluten till {port}: {hello}");
            StatusText.Text = $"Ansluten till GRBL på {port}. Inga kommandon skickas utan att du väljer det.";
            SetMachineControlsEnabled(true);
            await ReadActiveWcsAsync(client);
        }
        catch (Exception ex)
        {
            client.Dispose();
            SetConnectionBanner("EJ ANSLUTEN", "Anslutningen misslyckades.", Color.FromRgb(83, 60, 52));
            Error($"Kunde inte ansluta till {port}: {ex.Message}");
        }
        finally { ConnectButton.IsEnabled = true; }
    }

    internal void DisconnectGrbl(string reason)
    {
        var client = _grbl;
        _grbl = null;
        if (client is not null)
        {
            try { client.Disconnect(); } catch (Exception) { }
            client.Dispose();
        }
        ConnectButton.Content = "Anslut";
        SetConnectionBanner("EJ ANSLUTEN", reason, Color.FromRgb(83, 60, 52));
        LiveStateText.Text = "–";
        _jogHeld = false;
        SetMachineControlsEnabled(false);
        Log(reason);
    }

    private void SetConnectionBanner(string title, string detail, Color color)
    {
        ConnectionBanner.Background = new SolidColorBrush(color);
        ConnectionStateText.Text = title;
        ConnectionDetailText.Text = detail;
    }

    private double[]? _lastWco;

    /// <summary>Shows state, MPos and WPos. WPos = MPos − WCO (report.c); WCO is only sent periodically.</summary>
    private void ShowStatus(GrblStatus s)
    {
        if (_grbl is null) return;
        if (s.Wco is not null) _lastWco = s.Wco;
        LiveStateText.Text = s.SubState is null ? s.State : $"{s.State}:{s.SubState}";
        LiveStateText.Foreground = s.State switch
        {
            "Alarm" => Brushes.OrangeRed, "Idle" => Brushes.LightGreen,
            "Run" or "Jog" or "Home" => Brushes.Gold, _ => Brushes.White
        };
        double[]? mpos = s.MPos, wpos = s.WPos;
        if (mpos is null && wpos is not null && _lastWco is not null) mpos = Sub(wpos, _lastWco, add: true);
        if (wpos is null && mpos is not null && _lastWco is not null) wpos = Sub(mpos, _lastWco, add: false);
        // Keep the computed MPos on the status so the jog travel check works whatever $10 reports.
        s.MPos ??= mpos;
        ShowDro(mpos, wpos);
    }

    private static double[] Sub(double[] a, double[] b, bool add) =>
        a.Select((v, i) => i < b.Length ? (add ? v + b[i] : v - b[i]) : v).ToArray();

    private static string Vec(double[]? v) => v is null ? "–" :
        string.Join("  ", v.Select(x => x.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(9)));

    private void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        Dispatcher.BeginInvoke(() =>
        {
            _grblLog.Add(line);
            if (_grblLog.Count > 2000) _grblLog.RemoveRange(0, 500);
            GrblLogAdded?.Invoke(line);
        });
    }

    /// <summary>
    /// MPos Z of work Z0 = WCO Z = G54 Z + G92 Z + TLO (verified: report.c WCO, $# output).
    /// Refused if units are inches ($13=1) or a work coordinate system other than G54 is active.
    /// Only fills the field; the user saves.
    /// </summary>
    private async void ReadZeroFromMachine_Click(object sender, RoutedEventArgs e)
    {
        if (Grbl is not GrblClient g) { Error("Anslut till maskinen först (Maskinkontroll-panelen)."); return; }
        try
        {
            var settings = await g.ReadSettingsAsync();
            if (settings.TryGetValue(13, out var inches) && inches == 1)
            { Error("Maskinen rapporterar i tum ($13=1). CNC Studio räknar i mm – värdet läses inte."); return; }
            var gc = await g.SendLineAsync("$G");
            var modes = gc.Lines.FirstOrDefault(l => l.Raw.StartsWith("[GC:", StringComparison.Ordinal))?.Raw ?? "";
            if (!modes.Contains(" G54 ") && !modes.Contains(" G54]"))
            { Error($"Aktivt koordinatsystem är inte G54 ({modes}). Värdet läses inte."); return; }
            var p = await g.ReadParametersAsync();
            if (!p.TryGetValue("G54", out var g54) || g54.Length < 3)
            { Error("Maskinen svarade inte med G54 i $#."); return; }
            double g92 = p.TryGetValue("G92", out var o) && o.Length >= 3 ? o[2] : 0;
            double tlo = p.TryGetValue("TLO", out var t) && t.Length >= 1 ? t[0] : 0;
            double stockTop = g54[2] + g92 + tlo;
            string F3(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
            if (stockTop >= 0)
            { Error($"G54 Z + G92 Z + TLO = {F3(stockTop)}. Det ska vara negativt (under Z-brytaren) – är Z-nollan satt och maskinen homad?"); return; }
            if (AppDialog.Show(this,
                    $"MPos Z vid materialytan = {F3(stockTop)} mm\n(G54 Z {F3(g54[2])} + G92 Z {F3(g92)} + verktygslängd {F3(tlo)})\n\n" +
                    "Gäller bara om maskinen är homad med $H och Z-nollan är satt på materialets ovansida.\n\nAnvänd värdet?",
                    "Läs Z-nolla från maskinen", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            ZMaxBox.Text = F3(stockTop);
            StatusText.Text = "MPos Z vid materialytan inläst från maskinen. Klicka Spara Z-höjder för att använda det.";
        }
        catch (Exception ex) { Error("Kunde inte läsa från maskinen: " + ex.Message); }
    }

    private LimitSwitchTestWindow? _limitTest;

    private void OpenLimitTest_Click(object sender, RoutedEventArgs e)
    {
        if (Grbl is not GrblClient g) { Error("Anslut till maskinen först (Maskinkontroll-panelen)."); return; }
        if (_limitTest is { IsLoaded: true }) { _limitTest.Activate(); return; }
        var w = new LimitSwitchTestWindow(g) { Owner = this };
        w.Closed += (_, _) =>
        {
            if (w.HardLimitsLeftOff)
            {
                SetConnectionBanner("HÅRDA GRÄNSER AV", "$21=0 kunde inte återställas. Skriv $21=1 i konsolen innan körning.",
                    Color.FromRgb(150, 46, 38));
                Log("VARNING: hårda gränser ($21) är fortfarande avstängda.");
            }
            else if (w.AllPassed) Log("Brytartest: X, Y och Z godkända.");
        };
        _limitTest = w;
        w.Show();
    }

    private void OpenConsole_Click(object sender, RoutedEventArgs e)
    {
        if (_console is { IsLoaded: true }) { _console.Activate(); return; }
        _console = new GrblConsoleWindow(this) { Owner = this };
        _console.Show();
    }
}
