using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Sends single commands to GRBL. Lines go through the send-response queue (one at a time);
/// real-time commands (?, !, ~, Ctrl-X) are single bytes from the buttons. $X asks first.
/// </summary>
public partial class GrblConsoleWindow : Window
{
    /// <summary>$H only answers when homing has finished.</summary>
    private static readonly TimeSpan HomingTimeout = TimeSpan.FromMinutes(2);

    private readonly MainWindow _main;
    private readonly List<string> _history = new();
    private int _historyIndex;
    private bool _busy;

    public GrblConsoleWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        foreach (var line in main.GrblLog) LogBox.AppendText(line + "\n");
        LogBox.ScrollToEnd();
        main.GrblLogAdded += OnLog;
        Closed += (_, _) => main.GrblLogAdded -= OnLog;
        UpdateState();
        Loaded += (_, _) => InputBox.Focus();
    }

    private void OnLog(string line)
    {
        LogBox.AppendText(line + "\n");
        LogBox.ScrollToEnd();
        UpdateState();
    }

    private void UpdateState()
    {
        var g = _main.Grbl;
        StateLine.Text = g is null ? "Inte ansluten – anslut i Maskinkontroll-panelen."
            : $"Ansluten · läge {g.LastStatus?.State ?? "?"}" + (_busy ? " · väntar på svar…" : "");
        SendButton.IsEnabled = g is not null && !_busy;
    }

    private GrblClient? Client()
    {
        var g = _main.Grbl;
        if (g is null) MessageBox.Show(this, "Inte ansluten till GRBL.", "GRBL-konsol", MessageBoxButton.OK, MessageBoxImage.Information);
        return g;
    }

    private async Task SendAsync(string line, TimeSpan? timeout = null)
    {
        var g = Client();
        if (g is null) return;
        if (GrblProtocol.CheckCommandLine(line) is string problem)
        { MessageBox.Show(this, problem, "GRBL-konsol", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _busy = true; UpdateState();
        try { await g.SendLineAsync(line, timeout: timeout); }
        catch (Exception ex) { OnLog($"{DateTime.Now:HH:mm:ss}  ! {ex.Message}"); }
        finally { _busy = false; UpdateState(); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var line = InputBox.Text.Trim();
        if (line.Length == 0) return;
        if (line.Equals("$X", StringComparison.OrdinalIgnoreCase)) { Unlock_Click(sender, e); return; }
        _history.Add(line); _historyIndex = _history.Count;
        InputBox.Clear();
        await SendAsync(line, line.Equals("$H", StringComparison.OrdinalIgnoreCase) ? HomingTimeout : null);
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (_history.Count == 0) return;
        if (e.Key == Key.Up) { _historyIndex = Math.Max(0, _historyIndex - 1); InputBox.Text = _history[_historyIndex]; e.Handled = true; }
        else if (e.Key == Key.Down)
        {
            _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
            InputBox.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
            e.Handled = true;
        }
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private async void Quick_Click(object sender, RoutedEventArgs e) =>
        await SendAsync((string)((Button)sender).Tag);

    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        if (Client() is null) return;
        if (MessageBox.Show(this, "Kör homing ($H)?\n\nMaskinen rör sig: först Z uppåt mot brytaren, sedan X och Y mot sina brytare. " +
                "Kontrollera att inget är i vägen.", "Homing", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await SendAsync("$H", HomingTimeout);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (Client() is null) return;
        if (MessageBox.Show(this, "Lås upp utan homing ($X)?\n\nGRBL varnar: positionen är troligen förlorad. Maskinkoordinaterna och " +
                "Z-brytarens läge är då OKÄNDA och CNC Studios Z-kontroller gäller inte. Använd bara i nödfall – kör hellre $H.",
                "Lås upp utan homing", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        InputBox.Clear();
        await SendAsync("$X");
    }

    /// <summary>Shows one status report in the log (background polling is not logged).</summary>
    private async void Status_Click(object sender, RoutedEventArgs e)
    {
        var g = Client();
        if (g is null) return;
        OnLog($"{DateTime.Now:HH:mm:ss}  → ?");
        var s = await g.RequestStatusAsync(TimeSpan.FromSeconds(1));
        string V(double[]? v) => v is null ? "" : string.Join(",", v.Select(x => x.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)));
        OnLog($"{DateTime.Now:HH:mm:ss}  ← " + (s is null ? "ingen status inom 1 s"
            : $"{s.State}{(s.SubState is null ? "" : ":" + s.SubState)}" +
              (s.MPos is null ? "" : $"  MPos {V(s.MPos)}") + (s.WPos is null ? "" : $"  WPos {V(s.WPos)}") +
              (s.Pins is null ? "" : $"  Pn {s.Pins}")));
    }
    private void Hold_Click(object sender, RoutedEventArgs e) => Realtime(GrblProtocol.FeedHold);
    private void Resume_Click(object sender, RoutedEventArgs e) => Realtime(GrblProtocol.CycleStartResume);
    private void Reset_Click(object sender, RoutedEventArgs e) => Realtime(GrblProtocol.SoftReset);

    private void Realtime(byte command)
    {
        var g = Client();
        if (g is null) return;
        try { g.SendRealtime(command); }
        catch (Exception ex) { OnLog($"{DateTime.Now:HH:mm:ss}  ! {ex.Message}"); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => LogBox.Clear();
}
