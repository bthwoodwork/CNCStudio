using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Program → Starta från rad…: the program's lines, a line number and the program's own section headers to jump to,
/// and – for the line chosen – where it really starts (the nearest safe point) and how the machine gets there.
/// </summary>
public sealed class StartFromLineWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x8C, 0xA3, 0xBA), Card = B(0x1C, 0x2A, 0x39),
        Bad = B(0xFF, 0x7A, 0x86), Mark = B(0x2E, 0x5A, 0x8A);

    public ResumePlan? Result { get; private set; }

    private readonly IReadOnlyList<string> _lines;
    private readonly Func<int, ResumePlan> _plan;
    private readonly ListBox _list = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12.5, Background = B(0x10, 0x20, 0x2E), Foreground = Ink,
        BorderBrush = B(0x3C, 0x51, 0x67) };
    private readonly TextBox _number = new() { Width = 90, Padding = new Thickness(4, 2, 4, 2) };
    private readonly ComboBox _sections = new() { MinWidth = 260, MaxDropDownHeight = 400 };
    private readonly TextBlock _info = new() { Foreground = Ink, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap };
    private bool _syncing;

    public StartFromLineWindow(IReadOnlyList<string> lines, string name, Func<int, ResumePlan> plan)
    {
        _lines = lines; _plan = plan;
        Title = "Starta från rad";
        Width = 760; Height = 720; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        VirtualizingPanel.SetIsVirtualizing(_list, true);
        _list.ItemsSource = lines.Select((l, i) => $"{i + 1,6}  {l}").ToList();
        _list.SelectionChanged += (_, _) => { if (!_syncing && _list.SelectedIndex >= 0) Choose(_list.SelectedIndex, fromList: true); };

        // The program's own comments are the sections: operations, passes, tools (CNC Studio and most CAM programs write them).
        foreach (var (l, i) in lines.Select((l, i) => (l.Trim(), i)).Where(x => x.Item1.StartsWith("(") && x.Item1.Length > 2))
            _sections.Items.Add(new ComboBoxItem { Content = $"Rad {i + 1}: {l.Trim('(', ')', ' ')}", Tag = i });
        _sections.SelectionChanged += (_, _) => { if (!_syncing && (_sections.SelectedItem as ComboBoxItem)?.Tag is int i) Choose(Math.Min(i + 1, _lines.Count - 1), fromList: false); };
        _number.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            if (int.TryParse(_number.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= _lines.Count) Choose(n - 1, fromList: false);
        };

        var top = new StackPanel { Margin = new Thickness(18, 14, 18, 6) };
        top.Children.Add(new TextBlock
        {
            Text = $"{name}: {lines.Count} rader. Välj raden programmet ska börja på – klicka i listan, skriv radnumret eller hoppa till en del. " +
                   "Mitt i en fräsning börjar det på närmaste säkra punkt före raden: en förflyttning på säker höjd, början av ett djuppass eller ned i det redan frästa spåret.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        var pick = new WrapPanel();
        pick.Children.Add(new Label { Content = "Rad", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        pick.Children.Add(_number);
        if (_sections.Items.Count > 0)
        {
            pick.Children.Add(new Label { Content = "Hoppa till", Foreground = Muted, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            pick.Children.Add(_sections);
        }
        top.Children.Add(pick);

        var ok = new Button { Content = "Starta härifrån…", FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Avbryt", IsCancel = true };
        ok.Click += (_, _) => { if (Current is { } p) { Result = p; DialogResult = true; } else _error.Text = "Välj en rad."; };
        var bottom = new StackPanel { Margin = new Thickness(18, 6, 18, 14) };
        bottom.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Child = _info });
        bottom.Children.Add(_error);
        bottom.Children.Add(new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { ok, cancel } });

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(bottom);
        root.Children.Add(new Border { Margin = new Thickness(18, 0, 18, 0), Child = _list });
        Content = root;
        _info.Text = "Ingen rad vald.";
    }

    private ResumePlan? Current { get; set; }

    private void Choose(int index, bool fromList)
    {
        _syncing = true;
        try
        {
            _number.Text = (index + 1).ToString(CultureInfo.InvariantCulture);
            var plan = _plan(index);
            Current = plan;
            // Show where it really starts: that line is selected in the list.
            int start = plan.FromBeginning ? 0 : plan.StartIndex;
            _list.SelectedIndex = start;
            _list.ScrollIntoView(_list.SelectedItem);
            _info.Text = plan.Description +
                (plan.FromBeginning ? "" : "\n\nSå kör maskinen in:\n" + string.Join("   ", plan.Preamble) +
                 $"\n\nRad 1–{start} körs inte. Lägen som G21/G90, nollpunkt, varvtal och matning sätts av inkörningen.");
            _error.Text = "";
        }
        catch (Exception ex) { _error.Text = ex.Message; Current = null; }
        finally { _syncing = false; }
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
