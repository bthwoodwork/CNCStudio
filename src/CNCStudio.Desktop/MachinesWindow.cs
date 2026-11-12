using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Maskin → Maskiner: all machines (one profile each, the demo machine included) with their size, home
/// corner and port. Choose, edit, add and remove machines here, and reach what belongs to one machine: its
/// $$ copies and the steps/mm calibration (the connected machine only). Machines cannot be switched while connected.
/// </summary>
public sealed class MachinesWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Chosen = B(0x1F, 0x3A, 0x55), Good = B(0x7C, 0xD9, 0x9C), Demo = B(0xC9, 0xA8, 0xF0), Warn = B(0xFF, 0xD3, 0x9F);

    private readonly StackPanel _list = new();
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Foreground = Warn, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Func<string?> _chosenId;
    private readonly Func<string?> _connectedId;
    private readonly Action<MachineProfile> _use, _edit;
    private readonly Action<MachineProfile, Window> _calibrate, _backups;
    private readonly Func<MachineProfile, bool> _delete;
    private readonly Action _create;

    /// <param name="chosenId">The machine the project uses now.</param>
    /// <param name="connectedId">The connected machine, null when not connected.</param>
    public MachinesWindow(Func<string?> chosenId, Func<string?> connectedId, Action<MachineProfile> use,
        Action<MachineProfile> edit, Func<MachineProfile, bool> delete, Action create,
        Action<MachineProfile, Window> calibrate, Action<MachineProfile, Window> backups)
    {
        _chosenId = chosenId; _connectedId = connectedId; _use = use; _edit = edit; _delete = delete; _create = create;
        _calibrate = calibrate; _backups = backups;
        Title = "Maskiner";
        Width = 720; SizeToContent = SizeToContent.Height; MaxHeight = 820; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        var add = Btn("＋ Ny maskin", B(0x24, 0x61, 0x8C));
        add.Click += (_, _) => { _create(); Build(); };
        var close = Btn("Stäng", B(0x34, 0x47, 0x5A)); close.IsCancel = true; close.Click += (_, _) => Close();

        Content = new StackPanel { Margin = new Thickness(18), Children =
        {
            new TextBlock { Text = "MASKINER", FontSize = 18, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Varje maskin har en egen maskinprofil – demomaskinen också. Den valda maskinen är den projektet och nya projekt räknar med. " +
                                   "Ansluter du på en port väljs maskinen som senast använde den; DEMO-porten väljer alltid demomaskinen.",
                Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) },
            new ScrollViewer { Content = _list, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            _note,
            new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { add, close } },
        } };
        Build();
    }

    private void Build()
    {
        _list.Children.Clear();
        var connected = _connectedId();
        _note.Text = connected is null ? "" : "Ansluten: maskinen kan inte bytas förrän du kopplat från. Den anslutna maskinens inställningar kan ändras.";
        var all = AppStorage.Current.Machines.All();
        int real = all.Count(x => !MachineLibrary.IsDemo(x));
        foreach (var m in all) _list.Children.Add(Row(m, connected, lastReal: real <= 1));
    }

    private Border Row(MachineProfile m, string? connected, bool lastReal)
    {
        bool chosen = m.Id == _chosenId(), isConnected = m.Id == connected, demo = MachineLibrary.IsDemo(m);
        var title = new WrapPanel();
        title.Children.Add(new TextBlock { Text = m.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) });
        if (demo) title.Children.Add(Badge("DEMO", Demo));
        if (chosen) title.Children.Add(Badge("VALD", Good));
        if (isConnected) title.Children.Add(Badge("ANSLUTEN", Good));

        string corner = m.HomeCorner switch
        {
            HomeCorner.BackLeft => "bak vänster", HomeCorner.BackRight => "bak höger",
            HomeCorner.FrontLeft => "fram vänster", _ => "fram höger"
        };
        var details = new TextBlock
        {
            Text = $"X {F(m.WorkWidthMm)} × Y {F(m.WorkLengthMm)} × Z {F(m.ZTravelMm)} mm · hem {corner} · " +
                   $"port {(demo ? "DEMO" : m.Port ?? "ingen ännu")} · {(m.CalibratedToGrbl ? "jämförd med maskinens $$" : "ej jämförd med $$")}",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6)
        };

        var buttons = new WrapPanel();
        bool canSwitch = connected is null;
        if (!chosen)
        {
            var use = Btn("Välj", B(0x24, 0x61, 0x8C)); use.IsEnabled = canSwitch;
            use.Click += (_, _) => { _use(m); Build(); };
            buttons.Children.Add(use);
        }
        var edit = Btn("Inställningar…", B(0x34, 0x47, 0x5A)); edit.IsEnabled = canSwitch || isConnected;
        edit.Click += (_, _) => { _edit(m); Build(); };
        buttons.Children.Add(edit);
        var copies = Btn("$$-kopior…", B(0x34, 0x47, 0x5A));
        copies.ToolTip = $"Säkerhetskopior av {m.Name}s GRBL-inställningar. Jämföra och återställa går när maskinen är ansluten.";
        copies.Click += (_, _) => _backups(m, this);
        buttons.Children.Add(copies);
        var calibrate = Btn("Kalibrera steg/mm…", B(0x34, 0x47, 0x5A)); calibrate.IsEnabled = isConnected;
        calibrate.ToolTip = isConnected ? "Flytta en känd sträcka, mät och skriv nya $100–$102 till maskinen."
                                        : $"Anslut till {m.Name} först – kalibreringen görs på den anslutna maskinen.";
        ToolTipService.SetShowOnDisabled(calibrate, true);
        calibrate.Click += (_, _) => { _calibrate(m, this); Build(); };
        buttons.Children.Add(calibrate);
        if (!demo)
        {
            var del = Btn("Ta bort…", B(0x5A, 0x34, 0x3A)); del.IsEnabled = canSwitch && !lastReal;
            if (lastReal) { del.ToolTip = "Det måste finnas minst en maskin."; ToolTipService.SetShowOnDisabled(del, true); }
            del.Click += (_, _) => { if (_delete(m)) Build(); };
            buttons.Children.Add(del);
        }

        return new Border
        {
            Background = chosen ? Chosen : Card, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8), BorderBrush = chosen ? B(0x3E, 0x8E, 0xD0) : B(0x2C, 0x3E, 0x52), BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { title, details, buttons } },
            ToolTip = m.Name
        };
    }

    private static Border Badge(string text, Brush color) => new()
    {
        BorderBrush = color, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = color, FontSize = 10.5, FontWeight = FontWeights.SemiBold }
    };

    private static Button Btn(string text, Brush background) => new()
    {
        Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0),
        Foreground = Brushes.White, Background = background, BorderBrush = B(0x50, 0x67, 0x83)
    };

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
