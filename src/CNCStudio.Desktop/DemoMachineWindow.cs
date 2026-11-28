using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Demomaskinens panel: press the demo machine's X, Y and Z limit switches by hand and cut its power, to practise
/// what happens on the real machine – a switch with hard limits on ($21=1) gives ALARM:1 and needs a reset; a power
/// cut restarts the board, which then counts from 0 and is locked until homing ($22=1). Shows the switches, the
/// state and GRBL's position live. Only works while connected to the demo machine (DEMO in the port list).
/// </summary>
public sealed class DemoMachineWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Off = B(0x3A, 0x4C, 0x60), On = B(0xFF, 0x7A, 0x5A), Warn = B(0xFF, 0xD3, 0x9F);
    private static readonly string[] Axes = { "X", "Y", "Z" };

    private readonly MainWindow _main;
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap, Foreground = Soft };
    private readonly ToggleButton[] _switches = new ToggleButton[3];
    private readonly Border[] _leds = new Border[3];
    private readonly TextBlock[] _switchNotes = new TextBlock[3];
    private readonly TextBlock _position = new() { FontFamily = new FontFamily("Consolas"), Foreground = Ink };
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _power;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _syncing;

    public DemoMachineWindow(MainWindow main)
    {
        _main = main;
        Title = "Demomaskinen";
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        var switches = new StackPanel();
        for (int i = 0; i < 3; i++)
        {
            int axis = i;
            _leds[i] = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Off, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            _switches[i] = new ToggleButton { Content = $"Tryck {Axes[i]}-brytaren", Width = 150, Padding = new Thickness(10, 5, 10, 5),
                ToolTip = $"Håller {Axes[i]}-brytaren intryckt tills du klickar igen – som att trycka på den med fingret." };
            _switches[i].Checked += (_, _) => Press(axis, true);
            _switches[i].Unchecked += (_, _) => Press(axis, false);
            _switchNotes[i] = new TextBlock { Foreground = Muted, FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            switches.Children.Add(new DockPanel { Margin = new Thickness(0, 2, 0, 2), Children = { _leds[i], _switches[i], _switchNotes[i] } });
        }
        switches.Children.Add(new TextBlock { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = "Med hårda gränser på ($21=1) stoppar GRBL när en brytare ändras – också när maskinen står still – och ger ALARM:1. " +
                   "Släpp brytaren och återställ (Ctrl+X) innan maskinen tar emot kommandon igen." });

        _power = Btn("Strömavbrott – slå av och på", B(0x8A, 0x2E, 0x26));
        _power.ToolTip = "Kortet startar om där vagnen står: positionen är förlorad och maskinen är låst tills homing ($22=1). Ett program som körs avbryts.";
        _power.Click += (_, _) => PowerCut();
        var power = new StackPanel { Children =
        {
            _power,
            new TextBlock { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                Text = "Vagnen står kvar, men GRBL räknar från 0 igen och vet inte var den är. Ett avbrutet jobb kan återupptas under Program." }
        } };

        var topmost = new CheckBox { Content = "Alltid överst", Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        topmost.Checked += (_, _) => Topmost = true; topmost.Unchecked += (_, _) => Topmost = false;
        var close = Btn("Stäng", B(0x34, 0x47, 0x5A)); close.IsCancel = true; close.Click += (_, _) => Close();

        Content = new StackPanel { Margin = new Thickness(16), Children =
        {
            new TextBlock { Text = "DEMOMASKINEN", FontSize = 18, FontWeight = FontWeights.Bold },
            _header,
            new Border { Height = 10 },
            Section("BRYTARE", switches),
            Section("STRÖM", power),
            Section("GRBL:S POSITION (MPos)", _position),
            _status,
            new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { close, topmost } },
        } };

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _timer.Start(); Refresh(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void Press(int axis, bool pressed)
    {
        if (_syncing || _main.DemoMachine is not SimulatedGrbl sim) return;
        sim.PressSwitch(axis, pressed);
        _status.Text = pressed ? $"{Axes[axis]}-brytaren är intryckt." : $"{Axes[axis]}-brytaren är släppt.";
        Refresh();
    }

    private void PowerCut()
    {
        if (_main.DemoMachine is not SimulatedGrbl sim) return;
        sim.PowerCycle();
        _status.Text = $"Strömavbrott {DateTime.Now:HH:mm:ss}: kortet startade om och tappade sin position. Kör homing ($H).";
        _main.ShowStatus("Demomaskin: strömavbrott simulerat – maskinen startade om och tappade sin position.");
        Refresh();
    }

    private void Refresh()
    {
        var sim = _main.DemoMachine;
        bool connected = sim is not null;
        foreach (var t in _switches) t.IsEnabled = connected;
        _power.IsEnabled = connected;
        if (sim is null)
        {
            _header.Text = "Inte ansluten till demomaskinen. Välj DEMO (simulerad maskin) i portlistan uppe till vänster och anslut.";
            _header.Foreground = Warn;
            foreach (var l in _leds) l.Background = Off;
            foreach (var n in _switchNotes) n.Text = "";
            _position.Text = "–";
            return;
        }
        var s = sim.Snapshot();
        _header.Foreground = Soft;
        _header.Text = $"Ansluten · läge {s.State}" + (s.CriticalAlarm ? " – återställ (Ctrl+X)" : "") +
                       $" · {(s.Homed ? "homad: vet var vagnen är" : "inte homad: vet inte var vagnen är")}";
        _syncing = true;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                _switches[i].IsChecked = s.PressedByHand[i];
                _leds[i].Background = s.Switches[i] ? On : Off;
                _switchNotes[i].Text = s.PressedByHand[i] ? "intryckt för hand"
                    : s.Switches[i] ? "vagnen står på brytaren" : "";
                _switchNotes[i].Foreground = s.Switches[i] ? On : Muted;
            }
        }
        finally { _syncing = false; }
        string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(9);
        _position.Text = $"X{F(s.MPos[0])}   Y{F(s.MPos[1])}   Z{F(s.MPos[2])}" + (s.Pins.Length > 0 ? $"   Pn:{s.Pins}" : "");
    }

    private static Border Section(string title, UIElement content) => new()
    {
        Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 10),
        Child = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 11.5, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 0, 0, 6) }, content } }
    };

    private static Button Btn(string text, Brush background) => new()
    {
        Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Left,
        Foreground = Brushes.White, Background = background, BorderBrush = B(0x50, 0x67, 0x83)
    };

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
