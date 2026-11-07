using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Gamepad settings: lockout and second-action buttons, an action and a second action per button, which
/// stick moves which axis, invert, deadzone and jog speeds. Pressed buttons light up live, like in gSender,
/// so it is easy to see which is which.
/// </summary>
public sealed class GamepadWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Lit = B(0x2E, 0x7A, 0x4A), Bad = B(0xFF, 0x7A, 0x86);

    public GamepadProfile Result { get; private set; }

    private readonly CheckBox _enabled = new() { Content = "Styr maskinen med gamepad", Foreground = Ink, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ComboBox _lockout = new(), _second = new(), _stickX = new(), _stickY = new(), _stickZ = new();
    private readonly CheckBox _invX = Inv(), _invY = Inv(), _invZ = Inv();
    private readonly TextBox _dead = Box(), _feedXY = Box(), _feedZ = Box();
    private readonly Dictionary<GamepadButton, (Border Name, ComboBox Action, ComboBox Second)> _rows = new();
    private readonly TextBlock _live = new() { Foreground = Soft, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(60) };

    public GamepadWindow(GamepadProfile profile)
    {
        Result = profile;
        Title = "Gamepad";
        Width = 720; Height = 820; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        foreach (var c in new[] { _lockout, _second })
        {
            c.Items.Add(new ComboBoxItem { Content = GamepadMapper.ButtonName(GamepadButton.None), Tag = GamepadButton.None });
            foreach (var b in GamepadProfile.AllButtons) c.Items.Add(new ComboBoxItem { Content = GamepadMapper.ButtonName(b), Tag = b });
        }
        foreach (var c in new[] { _stickX, _stickY, _stickZ })
            foreach (var (text, a) in new[] { ("Vänster spak ←→", GamepadStickAxis.LeftX), ("Vänster spak ↑↓", GamepadStickAxis.LeftY),
                         ("Höger spak ←→", GamepadStickAxis.RightX), ("Höger spak ↑↓", GamepadStickAxis.RightY) })
                c.Items.Add(new ComboBoxItem { Content = text, Tag = a });

        var buttonsGrid = new Grid();
        buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        void Header(string text, int col) { var t = new TextBlock { Text = text, Foreground = Muted, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(4, 0, 4, 4) }; Grid.SetColumn(t, col); buttonsGrid.Children.Add(t); }
        buttonsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Header("KNAPP", 0); Header("ÅTGÄRD", 1); Header("MED ANDRA FUNKTION", 2);
        int row = 1;
        foreach (var b in GamepadProfile.AllButtons)
        {
            buttonsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(2),
                Child = new TextBlock { Text = GamepadMapper.ButtonName(b), Foreground = Ink } };
            var action = Actions(); var second = Actions();
            Grid.SetRow(name, row); Grid.SetRow(action, row); Grid.SetColumn(action, 1); Grid.SetRow(second, row); Grid.SetColumn(second, 2);
            buttonsGrid.Children.Add(name); buttonsGrid.Children.Add(action); buttonsGrid.Children.Add(second);
            _rows[b] = (name, action, second);
            row++;
        }

        var ok = new Button { Content = "Spara", IsDefault = true, FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Avbryt", IsCancel = true };
        var reset = new Button { Content = "Återställ standard" };
        ok.Click += (_, _) => Save();
        reset.Click += (_, _) => Fill(new GamepadProfile());
        foreach (var b in new[] { ok, cancel, reset })
        {
            b.Padding = new Thickness(14, 6, 14, 6); b.Margin = new Thickness(0, 0, 8, 0); b.Foreground = Brushes.White;
            b.Background = b == ok ? B(0x24, 0x61, 0x8C) : B(0x34, 0x47, 0x5A); b.BorderBrush = B(0x50, 0x67, 0x83);
        }

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 8), Children =
        {
            new TextBlock { Text = "GAMEPAD", FontSize = 18, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Xbox-kompatibla kontroller (XInput). Tryck på en knapp – raden lyser grönt. Spakarna joggar steglöst: ju längre du för dem, desto fortare. " +
                                   "Spärrknappen måste hållas in för att något ska hända (utom STOPP), så att en tappad kontroll inte kan köra maskinen.",
                Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) },
            Wrap(new StackPanel { Children = { _live } }),
            _enabled,
            Wrap(new StackPanel { Children =
            {
                Pair("Spärrknapp (håll in)", _lockout, "Andra funktion (som Shift)", _second),
                Pair("Nollgräns spakar (0–1)", _dead, "Max jogg X/Y (mm/min)", _feedXY),
                Pair("Max jogg Z (mm/min)", _feedZ, null, null),
            } }),
            new TextBlock { Text = "SPAKAR", FontWeight = FontWeights.Bold, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 4, 0, 4) },
            Wrap(new StackPanel { Children = { Stick("X", _stickX, _invX), Stick("Y", _stickY, _invY), Stick("Z", _stickZ, _invZ) } }),
            new TextBlock { Text = "KNAPPAR", FontWeight = FontWeights.Bold, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 4, 0, 4) },
            Wrap(buttonsGrid),
            _error,
        } };
        var buttons = new WrapPanel { Margin = new Thickness(18, 6, 18, 14), Children = { ok, reset, cancel } };
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = root;

        Fill(profile);
        _timer.Tick += (_, _) => ShowLive();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void ShowLive()
    {
        var read = XInputGamepad.Read();
        if (read is null)
        {
            _live.Text = "Ingen gamepad ansluten. Anslut den (USB eller trådlöst) – den visas här direkt.";
            foreach (var r in _rows.Values) r.Name.Background = Brushes.Transparent;
            return;
        }
        var s = read.Value.State;
        string F(double v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        _live.Text = $"Ansluten (kontroll {read.Value.Index + 1}).  Vänster spak {F(s.LeftX)} / {F(s.LeftY)} · Höger spak {F(s.RightX)} / {F(s.RightY)}" +
                     $" · LT {s.LeftTrigger:0.00} · RT {s.RightTrigger:0.00}";
        foreach (var (b, r) in _rows) r.Name.Background = s.IsDown(b) ? Lit : Brushes.Transparent;
    }

    private void Fill(GamepadProfile p)
    {
        _enabled.IsChecked = p.Enabled;
        Select(_lockout, p.LockoutButton); Select(_second, p.SecondActionButton);
        Select(_stickX, p.StickX); Select(_stickY, p.StickY); Select(_stickZ, p.StickZ);
        _invX.IsChecked = p.InvertX; _invY.IsChecked = p.InvertY; _invZ.IsChecked = p.InvertZ;
        _dead.Text = F(p.Deadzone); _feedXY.Text = F(p.MaxJogFeedXY); _feedZ.Text = F(p.MaxJogFeedZ);
        foreach (var (b, r) in _rows)
        {
            Select(r.Action, p.Actions.GetValueOrDefault(b));
            Select(r.Second, p.SecondActions.GetValueOrDefault(b));
        }
    }

    private void Save()
    {
        bool P(TextBox t, out double v) => double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (!P(_dead, out var dead) || !P(_feedXY, out var fxy) || !P(_feedZ, out var fz)) { _error.Text = "Nollgräns och hastigheter måste vara tal."; return; }
        var p = new GamepadProfile
        {
            Enabled = _enabled.IsChecked == true,
            LockoutButton = (GamepadButton)((ComboBoxItem)_lockout.SelectedItem).Tag, SecondActionButton = (GamepadButton)((ComboBoxItem)_second.SelectedItem).Tag,
            Deadzone = dead, MaxJogFeedXY = fxy, MaxJogFeedZ = fz,
            StickX = (GamepadStickAxis)((ComboBoxItem)_stickX.SelectedItem).Tag, StickY = (GamepadStickAxis)((ComboBoxItem)_stickY.SelectedItem).Tag,
            StickZ = (GamepadStickAxis)((ComboBoxItem)_stickZ.SelectedItem).Tag,
            InvertX = _invX.IsChecked == true, InvertY = _invY.IsChecked == true, InvertZ = _invZ.IsChecked == true,
            Actions = new(), SecondActions = new(),
        };
        foreach (var (b, r) in _rows)
        {
            if ((GamepadAction)((ComboBoxItem)r.Action.SelectedItem).Tag is var a && a != GamepadAction.None) p.Actions[b] = a;
            if ((GamepadAction)((ComboBoxItem)r.Second.SelectedItem).Tag is var s && s != GamepadAction.None) p.SecondActions[b] = s;
        }
        if (p.Validate() is string err) { _error.Text = err; return; }
        if (p.LockoutButton == GamepadButton.None &&
            AppDialog.Show(this, "Utan spärrknapp kan en tappad eller oavsiktligt tryckt kontroll flytta maskinen.\n\nSpara ändå?", "Gamepad",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        if (!p.Actions.ContainsValue(GamepadAction.Stop) && !p.SecondActions.ContainsValue(GamepadAction.Stop))
        { _error.Text = "Ge någon knapp åtgärden STOPP – den ska alltid finnas på kontrollen."; return; }
        Result = p;
        DialogResult = true;
    }

    private static ComboBox Actions()
    {
        var c = new ComboBox { Margin = new Thickness(2) };
        foreach (var a in Enum.GetValues<GamepadAction>()) c.Items.Add(new ComboBoxItem { Content = GamepadMapper.ActionName(a), Tag = a });
        return c;
    }

    private static void Select<T>(ComboBox c, T value) where T : notnull =>
        c.SelectedItem = c.Items.OfType<ComboBoxItem>().FirstOrDefault(i => value.Equals(i.Tag)) ?? c.Items[0];

    private static FrameworkElement Pair(string a, Control ca, string? b, Control? cb)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 6, 0), Children = { Label(a), ca } };
        g.Children.Add(left);
        if (b is not null && cb is not null)
        {
            var right = new StackPanel { Margin = new Thickness(6, 0, 0, 0), Children = { Label(b), cb } };
            Grid.SetColumn(right, 1); g.Children.Add(right);
        }
        return g;
    }

    private static FrameworkElement Stick(string axis, ComboBox c, CheckBox inv)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new TextBlock { Text = "Axel " + axis, VerticalAlignment = VerticalAlignment.Center, Foreground = Ink };
        Grid.SetColumn(c, 1); Grid.SetColumn(inv, 2);
        g.Children.Add(t); g.Children.Add(c); g.Children.Add(inv);
        return g;
    }

    private static Border Wrap(UIElement child) => new() { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10), Child = child };
    private static TextBlock Label(string t) => new() { Text = t, Foreground = Muted, FontSize = 11 };
    private static CheckBox Inv() => new() { Content = "Invertera", Foreground = B(0xE8, 0xF1, 0xFA), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private static TextBox Box() => new() { Padding = new Thickness(5, 3, 5, 3) };
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
