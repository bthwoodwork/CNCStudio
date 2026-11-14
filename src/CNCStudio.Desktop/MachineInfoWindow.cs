using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Maskininformation (like gSender's Machine Information): firmware and connection, the active modes from $G
/// in plain words (unusual ones marked), the inputs from Pn: live, and Lås stegmotorerna ($1=255). A normal
/// window that can stay open beside the work (Alltid överst). Reading $G only happens when the machine is Idle
/// and no program, macro or jog is running.
/// </summary>
public sealed class MachineInfoWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Good = B(0x7C, 0xD9, 0x9C), Warn = B(0xFF, 0xD3, 0x9F), Off = B(0x3A, 0x4C, 0x60), On = B(0xFF, 0x7A, 0x5A);

    private readonly MainWindow _main;
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap, Foreground = Soft };
    private readonly StackPanel _modes = new(), _pins = new();
    private readonly TextBlock _modesNote = new() { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _lock = new() { Content = "Lås stegmotorerna ($1=255)", Foreground = Ink, Margin = new Thickness(0, 4, 0, 0),
        ToolTip = "Håller motorerna strömsatta även när maskinen står still, så att axlarna inte kan skjutas ur läge (t.ex. vid fräsbyte). Motorerna blir varmare." };
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string? _lastState;
    private bool _reading, _syncingLock;

    public MachineInfoWindow(MainWindow main)
    {
        _main = main;
        Title = "Maskininformation";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        var refresh = Btn("Läs lägen ($G)", B(0x34, 0x47, 0x5A));
        refresh.Click += async (_, _) => await ReadModes(force: true);
        var topmost = new CheckBox { Content = "Alltid överst", Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        topmost.Checked += (_, _) => Topmost = true; topmost.Unchecked += (_, _) => Topmost = false;
        var close = Btn("Stäng", B(0x34, 0x47, 0x5A)); close.IsCancel = true; close.Click += (_, _) => Close();
        _lock.Click += async (_, _) => await ToggleLock();

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        var left = Section("LÄGEN (ur $G)", new StackPanel { Children = { _modes, _modesNote } });
        var right = Section("INGÅNGAR (live)", _pins);
        right.Margin = new Thickness(8, 0, 0, 10);
        Grid.SetColumn(right, 1);
        columns.Children.Add(left); columns.Children.Add(right);

        Content = new StackPanel { Margin = new Thickness(16), Children =
        {
            new TextBlock { Text = "MASKININFORMATION", FontSize = 18, FontWeight = FontWeights.Bold },
            _header,
            new Border { Height = 10 },
            columns,
            Section("STEGMOTORER", new StackPanel { Children = { _lock } }),
            _status,
            new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { refresh, close, topmost } },
        } };

        _timer.Tick += async (_, _) => await Tick();
        Loaded += async (_, _) => { _timer.Start(); await Tick(); await ReadModes(force: true); await ReadLock(); };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task Tick()
    {
        var g = _main.Grbl;
        var st = g?.LastStatus;
        _header.Text = g is null
            ? "Inte ansluten – anslut uppe till vänster i huvudfönstret."
            : $"{_main.MachineName} · {_main.ConnectedFirmware ?? "okänd firmware"} · läge {st?.State ?? "–"}";
        ShowPins(st?.Pins, g is not null);
        // New modes are read when the machine has come to rest after running or jogging.
        var state = st?.State;
        if (state == "Idle" && _lastState is not null and not "Idle") { _lastState = state; await ReadModes(force: false); }
        _lastState = state;
        _lock.IsEnabled = g is not null && state is "Idle" or "Alarm";
    }

    private void ShowPins(string? pn, bool connected)
    {
        _pins.Children.Clear();
        foreach (var p in MachineInfo.ParsePins(pn))
        {
            var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = !connected ? Off : p.On ? On : Off, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _pins.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), Children =
            {
                dot, new TextBlock { Text = p.Name, Foreground = p.On && connected ? Ink : Soft },
                new TextBlock { Text = p.On && connected ? "  PÅ" : "", Foreground = On, FontWeight = FontWeights.Bold }
            } });
        }
    }

    private async Task ReadModes(bool force)
    {
        if (_reading) return;
        var g = _main.Grbl;
        if (g is null) { ShowModes(null, "Inte ansluten."); return; }
        if (!_main.CanQueryMachine || g.LastStatus?.State is not ("Idle" or "Alarm"))
        {
            if (force) _modesNote.Text = "Lägena läses när maskinen står still (Idle) och inget program, makro eller jogg pågår.";
            return;
        }
        _reading = true;
        try
        {
            var r = await g.SendLineAsync("$G");
            var modes = r.Lines.Select(l => MachineInfo.ParseModes(l.Raw)).FirstOrDefault(x => x is not null);
            ShowModes(modes, modes is null ? "Maskinen svarade inte med lägen." : $"Lästa {DateTime.Now:HH:mm:ss}.");
        }
        catch (Exception ex) { _modesNote.Text = "Kunde inte läsa $G: " + ex.Message; }
        finally { _reading = false; }
    }

    private void ShowModes(IReadOnlyList<ModalState>? modes, string note)
    {
        _modes.Children.Clear();
        foreach (var m in modes ?? Array.Empty<ModalState>())
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var group = new TextBlock { Text = m.Group, Foreground = Muted, Width = 110 };
            var code = new TextBlock { Text = m.Code, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.SemiBold, Width = 64, Foreground = m.Unusual ? Warn : Ink };
            DockPanel.SetDock(group, Dock.Left); DockPanel.SetDock(code, Dock.Left);
            row.Children.Add(group); row.Children.Add(code);
            row.Children.Add(new TextBlock { Text = m.Meaning, Foreground = m.Unusual ? Warn : Soft, TextWrapping = TextWrapping.Wrap });
            _modes.Children.Add(row);
        }
        _modesNote.Text = note + (modes?.Any(m => m.Unusual) == true ? " Gult = ett läge CNC Studios program inte räknar med." : "");
    }

    private async Task ReadLock()
    {
        if (_main.Grbl is not GrblClient g || !_main.CanQueryMachine) return;
        try
        {
            var s = await g.ReadSettingsAsync();
            _syncingLock = true;
            _lock.IsChecked = MachineInfo.MotorsLocked(s);
            _status.Text = s.TryGetValue(1, out var v) ? $"$1 (stegmotorernas vilotid) = {v.ToString("0", CultureInfo.InvariantCulture)}" + (v >= 255 ? " – alltid på." : " ms.") : "";
        }
        catch (Exception) { }
        finally { _syncingLock = false; }
    }

    /// <summary>Locks ($1=255) or unlocks (back to the value from before) – written with a copy of $$ first and read back.</summary>
    private async Task ToggleLock()
    {
        if (_syncingLock || _main.Grbl is not GrblClient g) return;
        bool wantLocked = _lock.IsChecked == true;
        if (!_main.CanQueryMachine) { _status.Text = "Vänta tills inget program, makro eller jogg pågår."; await ReadLock(); return; }
        try
        {
            var s = await g.ReadSettingsAsync();
            int before = s.TryGetValue(1, out var v) ? (int)Math.Round(v) : MachineInfo.DefaultIdleDelay;
            int value;
            if (wantLocked)
            {
                if (AppDialog.Show(this, "Lås stegmotorerna? $1 sätts till 255, så motorerna hålls strömsatta även när maskinen står still. " +
                        "Axlarna kan då inte skjutas ur läge, men motorerna blir varmare.\n\nEn kopia av inställningarna sparas först.",
                        "Lås stegmotorerna", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) != MessageBoxResult.OK) { await ReadLock(); return; }
                _main.SaveIdleDelayBeforeLock(before);
                value = MachineInfo.LockedIdleDelay;
            }
            else value = MachineInfo.UnlockValue(_main.IdleDelayBeforeLock());
            var r = await g.WriteSettingsAsync(new[] { (1, (double)value) });
            _status.Text = r.Success ? (wantLocked ? "Stegmotorerna är låsta ($1=255)." : $"Stegmotorerna släpps efter {value} ms stillastående ($1={value}).") : "Kunde inte skriva $1: " + r.Message;
        }
        catch (Exception ex) { _status.Text = "Kunde inte ändra $1: " + ex.Message; }
        await ReadLock();
    }

    private static Border Section(string title, UIElement content) => new()
    {
        Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 10),
        Child = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 11.5, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 0, 0, 6) }, content } }
    };

    private static Button Btn(string text, Brush background) => new()
    {
        Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0),
        Foreground = Brushes.White, Background = background, BorderBrush = B(0x50, 0x67, 0x83)
    };

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
