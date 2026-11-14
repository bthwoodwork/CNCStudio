using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Tangentbordsjogg: a key per action. Click an action's key and press the new one (with Ctrl/Shift/Alt if wanted);
/// × removes it. Two actions on one key, or a key the program already uses, are refused when saving.
/// </summary>
public sealed class KeyboardShortcutsWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Wait = B(0x2E, 0x5A, 0x8A), Bad = B(0xFF, 0x7A, 0x86);

    public KeyboardShortcuts Result { get; private set; }

    private KeyboardShortcuts _work;
    private readonly CheckBox _enabled = new() { Content = "Jogga och styr maskinen med tangentbordet", Foreground = Ink, Margin = new Thickness(0, 0, 0, 6) };
    private readonly CheckBox _onlyMachine = new() { Content = "Bara i arbetsytan Maskin (F3) – piltangenterna gör inget i Design och CAM", Foreground = Ink, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Dictionary<KeyAction, Button> _keyButtons = new();
    private readonly TextBlock _error = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 0, 18, 0) };
    private KeyAction? _capturing;

    public KeyboardShortcutsWindow(KeyboardShortcuts keys)
    {
        Result = keys;
        _work = keys.Normalized();
        Title = "Tangentbord";
        Width = 600; Height = 780; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 6) };
        body.Children.Add(new TextBlock
        {
            Text = "Håll en joggtangent inne för att jogga så länge den är nedtryckt; ett kort tryck tar ett steg (joggens steg och hastighet). " +
                   "Tangenterna verkar inte när du skriver i ett fält. Klicka på en tangent nedan och tryck den nya – med Ctrl, Shift eller Alt om du vill.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        body.Children.Add(_enabled);
        body.Children.Add(_onlyMachine);

        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        string? group = null;
        foreach (var a in Enum.GetValues<KeyAction>())
        {
            string g = KeyboardShortcuts.JogTag(a) is not null ? "JOGG" : a <= KeyAction.NextSpeed ? "HASTIGHET" : a <= KeyAction.Home ? "NOLLA OCH GÅ TILL" : "STOPP OCH PAUS";
            if (g != group)
            {
                group = g;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var h = new TextBlock { Text = g, Foreground = Muted, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(2, row == 0 ? 0 : 10, 0, 3) };
                Grid.SetRow(h, row++); grid.Children.Add(h);
            }
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = KeyboardShortcuts.ActionName(a), Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2) };
            var key = new Button { Margin = new Thickness(2), Padding = new Thickness(6, 3, 6, 3), Focusable = false, ToolTip = "Klicka och tryck den nya tangenten. Klicka igen för att ångra." };
            var action = a;
            key.Click += (_, _) => { _capturing = _capturing == action ? null : action; ShowKeys(); };
            var clear = new Button { Content = "×", Margin = new Thickness(2), Padding = new Thickness(8, 3, 8, 3), Focusable = false, ToolTip = "Ingen tangent" };
            clear.Click += (_, _) => { _work.Set(action, null); _capturing = null; ShowKeys(); };
            Grid.SetRow(name, row); Grid.SetRow(key, row); Grid.SetColumn(key, 1); Grid.SetRow(clear, row); Grid.SetColumn(clear, 2);
            grid.Children.Add(name); grid.Children.Add(key); grid.Children.Add(clear);
            _keyButtons[a] = key;
            row++;
        }
        body.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Child = grid });

        var ok = new Button { Content = "Spara", IsDefault = true, FontWeight = FontWeights.SemiBold, Focusable = false };
        var cancel = new Button { Content = "Avbryt", Focusable = false };
        var reset = new Button { Content = "Återställ standard", Focusable = false };
        ok.Click += (_, _) => Save();
        cancel.Click += (_, _) => DialogResult = false;
        reset.Click += (_, _) => { _work = KeyboardShortcuts.Defaults(); _capturing = null; Fill(); };
        var buttons = new WrapPanel { Margin = new Thickness(18, 6, 18, 14), Children = { ok, reset, cancel } };
        var bottom = new StackPanel { Children = { _error, buttons } };
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = root;

        // While waiting for a key every key is taken (Esc and Enter too), so they can be chosen as well.
        PreviewKeyDown += (_, e) =>
        {
            if (_capturing is not KeyAction a) { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } return; }
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (MainWindow.IsModifierKey(key)) return;
            _work.Set(a, MainWindow.ChordOf(key));
            _capturing = null;
            ShowKeys();
        };
        Fill();
    }

    private void Fill()
    {
        _enabled.IsChecked = _work.Enabled;
        _onlyMachine.IsChecked = _work.OnlyInMachineWorkspace;
        ShowKeys();
    }

    private void ShowKeys()
    {
        foreach (var (a, b) in _keyButtons)
        {
            bool waiting = _capturing == a;
            b.Content = waiting ? "Tryck en tangent …" : KeyboardShortcuts.Display(_work.ChordFor(a));
            b.Background = waiting ? Wait : null;
        }
        if (_capturing is null) _error.Text = string.Join("\n", _work.Validate());
    }

    private void Save()
    {
        _capturing = null;
        _work.Enabled = _enabled.IsChecked == true;
        _work.OnlyInMachineWorkspace = _onlyMachine.IsChecked == true;
        var problems = _work.Validate();
        if (problems.Count > 0) { _error.Text = string.Join("\n", problems); ShowKeys(); return; }
        if (_work.Enabled && _work.ChordFor(KeyAction.Stop) is null)
        { _error.Text = "Ge Stoppa joggen en tangent – den ska alltid finnas när man joggar med tangentbordet."; return; }
        Result = _work;
        DialogResult = true;
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
