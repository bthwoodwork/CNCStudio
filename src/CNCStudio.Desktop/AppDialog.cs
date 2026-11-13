using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CNCStudio.Desktop;

/// <summary>A problem with a concrete fix and, when possible, a button that takes the user there.</summary>
public sealed record DialogIssue(string Problem, string Fix, string? ActionLabel = null, Action? Action = null);

/// <summary>
/// Dark dialog in the app's own style, replacing the Windows MessageBox everywhere.
/// <see cref="Show"/> has the same arguments and results as MessageBox.Show, so it is a drop-in
/// replacement; Enter picks the default button and Esc the safe one (Cancel/No).
/// <see cref="ShowIssues"/> lists problems, each with a suggested fix and a "go there" button.
/// </summary>
public sealed class AppDialog : Window
{
    private static readonly Brush Panel = Frozen(0x1F, 0x2E, 0x3F);
    private static readonly Brush Footer = Frozen(0x19, 0x26, 0x34);
    private static readonly Brush Edge = Frozen(0x4A, 0x60, 0x78);
    private static readonly Brush TextMain = Frozen(0xE8, 0xF1, 0xFA);
    private static readonly Brush TextSoft = Frozen(0xB9, 0xCB, 0xDD);
    private static readonly Brush TextMuted = Frozen(0x8C, 0xA3, 0xBA);
    private static readonly Brush Card = Frozen(0x26, 0x38, 0x4D);
    private static readonly Brush Primary = Frozen(0x2E, 0x7D, 0xD7);
    private static readonly Brush PrimaryHover = Frozen(0x43, 0x92, 0xEA);
    private static readonly Brush Secondary = Frozen(0x34, 0x47, 0x5A);
    private static readonly Brush SecondaryHover = Frozen(0x40, 0x58, 0x70);

    private MessageBoxResult _result = MessageBoxResult.None;
    private MessageBoxResult _escapeResult = MessageBoxResult.None;
    private Action? _afterClose;

    private AppDialog(Window? owner, string title)
    {
        Title = title;
        if (owner is { IsLoaded: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Finish(_escapeResult); e.Handled = true; } };
    }

    // ---- Public API ----

    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.Information,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var d = new AppDialog(owner, title);
        var (accent, glyph) = Look(image);
        var body = new StackPanel();
        AddFormattedText(body, message);
        d.Build(title, accent, glyph, body, ButtonSet(buttons), defaultResult);
        d.ShowDialog();
        return d._result;
    }

    /// <summary>Like Show, but with the caller's own button texts (first = primary).</summary>
    public static MessageBoxResult Choose(Window? owner, string message, string title,
        IReadOnlyList<(string Text, MessageBoxResult Result)> buttons, MessageBoxImage image = MessageBoxImage.Question,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var d = new AppDialog(owner, title);
        var (accent, glyph) = Look(image);
        var body = new StackPanel();
        AddFormattedText(body, message);
        d.Build(title, accent, glyph, body, buttons, defaultResult);
        d.ShowDialog();
        return d._result;
    }

    /// <summary>
    /// Asks for a text (e.g. a name). With <paramref name="choices"/> the field is a list to pick from that
    /// can also be typed in (<paramref name="choicesOnly"/>: only the listed values). Null = cancelled.
    /// </summary>
    public static string? Prompt(Window? owner, string message, string title, string initial = "",
        IReadOnlyList<string>? choices = null, bool choicesOnly = false, string okText = "OK")
    {
        var d = new AppDialog(owner, title);
        var (accent, glyph) = Look(MessageBoxImage.Question);
        var body = new StackPanel();
        AddFormattedText(body, message);
        Control field;
        Func<string> read;
        Action focus;
        if (choices is null)
        {
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(6, 4, 6, 4), FontSize = 13 };
            field = box; read = () => box.Text;
            focus = () => { box.Focus(); box.SelectAll(); };
        }
        else
        {
            var combo = new ComboBox { ItemsSource = choices, IsEditable = !choicesOnly, Text = initial, Margin = new Thickness(0, 10, 0, 0), FontSize = 13 };
            if (choicesOnly) combo.SelectedItem = choices.FirstOrDefault(c => c == initial) ?? choices.FirstOrDefault();
            field = combo; read = () => (choicesOnly ? combo.SelectedItem as string : combo.Text) ?? "";
            focus = () => combo.Focus();
        }
        body.Children.Add(field);
        d.Build(title, accent, glyph, body, new[] { (okText, MessageBoxResult.OK), ("Avbryt", MessageBoxResult.Cancel) }, MessageBoxResult.OK);
        d.Loaded += (_, _) => focus();   // after Build's own Loaded, which focuses the OK button
        d.ShowDialog();
        return d._result == MessageBoxResult.OK ? read().Trim() : null;
    }

    /// <summary>
    /// Problems with fixes. Clicking an issue's action closes the dialog and then runs the action,
    /// so the main window can switch view and put focus on the right field.
    /// </summary>
    public static void ShowIssues(Window? owner, string title, string intro, IReadOnlyList<DialogIssue> issues,
        MessageBoxImage image = MessageBoxImage.Error, string? footnote = null)
    {
        var d = new AppDialog(owner, title);
        var (accent, glyph) = Look(image);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = intro, Foreground = TextMain, FontSize = 13.5,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        int n = 0;
        foreach (var issue in issues) body.Children.Add(d.IssueCard(issue, accent, ++n, issues.Count));
        if (footnote is not null)
            body.Children.Add(new TextBlock { Text = footnote, Foreground = TextMuted, FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        d.Build(title, accent, glyph, body, new[] { ("Stäng", MessageBoxResult.OK) }, MessageBoxResult.OK);
        d.ShowDialog();
        d._afterClose?.Invoke();
    }

    // ---- Layout ----

    private void Build(string title, Brush accent, string glyph, UIElement body,
        IReadOnlyList<(string Text, MessageBoxResult Result)> buttons, MessageBoxResult defaultResult)
    {
        var header = new DockPanel { Margin = new Thickness(20, 18, 12, 0), LastChildFill = true };
        var close = FlatButton("", () => Finish(_escapeResult));
        close.FontFamily = new FontFamily("Segoe MDL2 Assets"); close.FontSize = 10;
        close.Width = 30; close.Height = 26; close.VerticalAlignment = VerticalAlignment.Top;
        close.ToolTip = "Stäng (Esc)";
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = WithAlpha(accent, 40), BorderBrush = accent, BorderThickness = new Thickness(1.5),
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15,
                Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(new TextBlock { Text = title, Foreground = TextMain, FontSize = 16, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 520,
            Padding = new Thickness(66, 12, 24, 18) };

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 12, 16, 12) };
        var chosenDefault = defaultResult != MessageBoxResult.None && buttons.Any(b => b.Result == defaultResult)
            ? defaultResult : buttons[0].Result;
        _escapeResult = buttons.Any(b => b.Result == MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
            : buttons.Any(b => b.Result == MessageBoxResult.No) ? MessageBoxResult.No : buttons[^1].Result;
        Button? focus = null;
        for (int i = 0; i < buttons.Count; i++)
        {
            var (text, result) = buttons[i];
            bool primary = i == 0;
            var b = DialogButton(text, primary, () => Finish(result));
            b.Margin = new Thickness(8, 0, 0, 0);
            if (result == chosenDefault) { b.IsDefault = true; focus = b; }
            footer.Children.Add(b);
        }
        var footerBar = new Border { Background = Footer, CornerRadius = new CornerRadius(0, 0, 8, 8),
            BorderBrush = Frozen(0x2F, 0x42, 0x56), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };

        var layout = new DockPanel();
        var strip = new Border { Height = 3, Background = accent, CornerRadius = new CornerRadius(8, 8, 0, 0) };
        DockPanel.SetDock(strip, Dock.Top); layout.Children.Add(strip);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        DockPanel.SetDock(footerBar, Dock.Bottom); layout.Children.Add(footerBar);
        layout.Children.Add(scroll);

        Content = new Border
        {
            Margin = new Thickness(18), Background = Panel, BorderBrush = Edge, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), MinWidth = 420, MaxWidth = 640,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black },
            Child = layout
        };
        Loaded += (_, _) => { focus?.Focus(); };
    }

    private UIElement IssueCard(DialogIssue issue, Brush accent, int number, int count)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = issue.Problem, Foreground = TextMain, FontSize = 13,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var fix = new TextBlock { Foreground = TextSoft, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0) };
        fix.Inlines.Add(new System.Windows.Documents.Run("Gör så här: ") { FontWeight = FontWeights.SemiBold, Foreground = Frozen(0x7F, 0xD6, 0x9E) });
        fix.Inlines.Add(new System.Windows.Documents.Run(issue.Fix));
        stack.Children.Add(fix);
        if (issue.Action is not null)
        {
            var go = DialogButton((issue.ActionLabel ?? "Gå dit") + "  →", primary: true, () =>
            {
                _afterClose = issue.Action;
                Finish(MessageBoxResult.OK);
            });
            go.HorizontalAlignment = HorizontalAlignment.Left;
            go.Margin = new Thickness(0, 9, 0, 0);
            go.Padding = new Thickness(12, 5, 12, 5);
            stack.Children.Add(go);
        }
        var row = new DockPanel();
        var badge = new TextBlock { Text = count > 1 ? number.ToString() : "!", Foreground = accent, FontWeight = FontWeights.Bold,
            FontSize = 13, Width = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(stack);
        return new Border { Background = Card, BorderBrush = accent, BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 10, 14, 12), Margin = new Thickness(0, 0, 0, 8),
            Child = row };
    }

    /// <summary>
    /// Plain text with light structure: first paragraph as a lead line, "• " lines as bullets,
    /// ALL-CAPS lines ending with ':' as section headings, blank lines as spacing.
    /// </summary>
    private static void AddFormattedText(Panel target, string message)
    {
        bool lead = true;
        foreach (var raw in message.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (target.Children.Count > 0) { lead = false; target.Children.Add(new Border { Height = 8 }); }
                continue;
            }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("• ") || trimmed.StartsWith("- "))
            {
                var row = new DockPanel { Margin = new Thickness(2, 2, 0, 2) };
                var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = TextMuted,
                    Margin = new Thickness(0, 7, 9, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(new TextBlock { Text = trimmed[2..], Foreground = TextSoft, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
                target.Children.Add(row);
                lead = false;
            }
            else if (trimmed.EndsWith(':') && trimmed.Any(char.IsLetter) && trimmed.Where(char.IsLetter).All(char.IsUpper))
            {
                target.Children.Add(new TextBlock { Text = trimmed.TrimEnd(':'), Foreground = Frozen(0xA5, 0xC0, 0xD9),
                    FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 3) });
                lead = false;
            }
            else
            {
                target.Children.Add(new TextBlock { Text = trimmed, TextWrapping = TextWrapping.Wrap,
                    Foreground = lead ? TextMain : TextSoft, FontSize = lead ? 13.5 : 12.5,
                    FontWeight = lead ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 1, 0, 1) });
            }
        }
    }

    // ---- Helpers ----

    private void Finish(MessageBoxResult result) { _result = result; Close(); }

    private static IReadOnlyList<(string, MessageBoxResult)> ButtonSet(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => new[] { ("OK", MessageBoxResult.OK), ("Avbryt", MessageBoxResult.Cancel) },
        MessageBoxButton.YesNo => new[] { ("Ja", MessageBoxResult.Yes), ("Nej", MessageBoxResult.No) },
        MessageBoxButton.YesNoCancel => new[] { ("Ja", MessageBoxResult.Yes), ("Nej", MessageBoxResult.No), ("Avbryt", MessageBoxResult.Cancel) },
        _ => new[] { ("OK", MessageBoxResult.OK) }
    };

    private static (Brush Accent, string Glyph) Look(MessageBoxImage image) => image switch
    {
        MessageBoxImage.Error => (Frozen(0xFF, 0x5C, 0x6C), ""),     // also Stop/Hand
        MessageBoxImage.Warning => (Frozen(0xFF, 0xB0, 0x3B), ""),   // also Exclamation
        MessageBoxImage.Question => (Frozen(0x5E, 0xB0, 0xFF), ""),
        _ => (Frozen(0x5E, 0xB0, 0xFF), "")
    };

    private static Button DialogButton(string text, bool primary, Action click)
    {
        var b = new Button { Content = text, MinWidth = 92, Height = 32, Padding = new Thickness(16, 0, 16, 0),
            Foreground = Brushes.White, FontSize = 13, Cursor = Cursors.Hand,
            Background = primary ? Primary : Secondary };
        b.Template = RoundedTemplate(primary ? PrimaryHover : SecondaryHover);
        b.Click += (_, _) => click();
        return b;
    }

    private static Button FlatButton(string text, Action click)
    {
        var b = new Button { Content = text, Foreground = TextMuted, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        b.Template = RoundedTemplate(SecondaryHover);
        b.Click += (_, _) => click();
        return b;
    }

    private static ControlTemplate RoundedTemplate(Brush hover)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
        t.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Border.BackgroundProperty, hover, "Bd") } });
        t.Triggers.Add(new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true,
            Setters = { new Setter(Border.BorderBrushProperty, Frozen(0xDC, 0xEB, 0xFA), "Bd"),
                        new Setter(Border.BorderThicknessProperty, new Thickness(1), "Bd") } });
        return t;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush;
    }

    private static Brush WithAlpha(Brush brush, byte alpha)
    {
        var c = ((SolidColorBrush)brush).Color;
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); b.Freeze(); return b;
    }
}
