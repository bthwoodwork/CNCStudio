using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Rita → Text: the text, font, style, alignment and height, with a live preview and the size in mm.</summary>
public sealed class TextWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x8C, 0xA3, 0xBA), Card = B(0x1C, 0x2A, 0x39),
        Fill = B(0x7F, 0xB8, 0xE8), Bad = B(0xFF, 0x7A, 0x86);

    public TextSource? ResultInfo { get; private set; }
    public double ResultHeight { get; private set; }

    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 70, FontSize = 15, Padding = new Thickness(5),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _font = new() { IsEditable = false, MaxDropDownHeight = 420 };
    private readonly CheckBox _bold = new() { Content = "Fet", Foreground = Ink, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _italic = new() { Content = "Kursiv", Foreground = Ink, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _align = new() { Width = 110 };
    private readonly TextBox _height = new() { Width = 70, Padding = new Thickness(4, 2, 4, 2) };
    private readonly TextBox _spacing = new() { Width = 50, Padding = new Thickness(4, 2, 4, 2) };
    private readonly Path _preview = new() { Fill = Fill, Stretch = Stretch.Uniform };
    private readonly TextBlock _size = new() { Foreground = Muted, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap };

    public TextWindow(TextSource? existing, double heightMm, bool editing)
    {
        Title = editing ? "Redigera text" : "Text";
        Width = 640; Height = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
            _font.Items.Add(new ComboBoxItem { Content = f, FontFamily = new FontFamily(f), FontSize = 14 });
        foreach (var (t, a) in new[] { ("Vänster", TextAlign.Left), ("Centrerad", TextAlign.Center), ("Höger", TextAlign.Right) })
            _align.Items.Add(new ComboBoxItem { Content = t, Tag = a });

        var info = existing?.Clone() ?? new TextSource { Content = "Text", FontFamily = TextOutlines.FontInstalled("Arial") ? "Arial" : Fonts.SystemFontFamilies.First().Source };
        string? missing = TextOutlines.FontInstalled(info.FontFamily) ? null : info.FontFamily;
        _text.Text = info.Content;
        _font.SelectedItem = _font.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Content, info.FontFamily, StringComparison.OrdinalIgnoreCase))
                             ?? _font.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == "Arial") ?? _font.Items.Cast<ComboBoxItem>().FirstOrDefault();
        _bold.IsChecked = info.Bold; _italic.IsChecked = info.Italic;
        _align.SelectedItem = _align.Items.Cast<ComboBoxItem>().First(i => (TextAlign)i.Tag == info.Align);
        _height.Text = heightMm.ToString("0.##", CultureInfo.InvariantCulture);
        _spacing.Text = info.LineSpacing.ToString("0.##", CultureInfo.InvariantCulture);

        Label L(string t) => new() { Content = t, Foreground = Muted, Padding = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var style = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { _bold, _italic, L("Justering"), _align } };
        var size = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { L("Höjd (hela texten) mm"), _height, new Border { Width = 16 }, L("Radavstånd"), _spacing } };
        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 6) };
        body.Children.Add(new TextBlock { Text = "TEXT", Foreground = Muted, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        body.Children.Add(_text);
        body.Children.Add(new TextBlock { Text = "TYPSNITT", Foreground = Muted, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4) });
        body.Children.Add(_font);
        if (missing is not null)
            body.Children.Add(new TextBlock { Text = $"Typsnittet \"{missing}\" finns inte på den här datorn – välj ett annat (formen i projektet ändras inte förrän du sparar).", Foreground = Bad, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        body.Children.Add(style);
        body.Children.Add(size);
        body.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(14), Margin = new Thickness(0, 12, 0, 0), Height = 170, Child = _preview });
        body.Children.Add(_size);
        body.Children.Add(new TextBlock { Text = "Tips: fickfräs texten (gravyr) eller skär ut den. Tunna bokstäver kräver en smal fräs – CAM-kontrollen visar det som inte nås. Dubbelklicka på texten i ritytan för att ändra den.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 11.5 });

        var ok = new Button { Content = editing ? "Spara" : "Lägg till", IsDefault = false, FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Avbryt", IsCancel = true };
        ok.Click += (_, _) => Save();
        var buttons = new WrapPanel { Margin = new Thickness(18, 6, 18, 14), Children = { ok, cancel } };
        var bottom = new StackPanel { Children = { new Border { Padding = new Thickness(18, 0, 18, 0), Child = _error }, buttons } };
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = root;

        _text.TextChanged += (_, _) => Update();
        _font.SelectionChanged += (_, _) => Update();
        _align.SelectionChanged += (_, _) => Update();
        _bold.Click += (_, _) => Update(); _italic.Click += (_, _) => Update();
        _height.TextChanged += (_, _) => Update(); _spacing.TextChanged += (_, _) => Update();
        Loaded += (_, _) => { _text.Focus(); _text.SelectAll(); };
        Update();
    }

    private TextSource Read() => new()
    {
        Content = _text.Text.Replace("\r\n", "\n").TrimEnd('\n'),
        FontFamily = (_font.SelectedItem as ComboBoxItem)?.Content as string ?? "Arial",
        Bold = _bold.IsChecked == true, Italic = _italic.IsChecked == true,
        Align = (_align.SelectedItem as ComboBoxItem)?.Tag is TextAlign a ? a : TextAlign.Left,
        LineSpacing = P(_spacing, out var s) ? s : double.NaN,
    };

    private static bool P(TextBox t, out double v) => double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private void Update()
    {
        if (!IsInitializedFully) return;
        var info = Read();
        _error.Text = info.Validate() ?? "";
        if (_error.Text.Length > 0) { _preview.Data = null; _size.Text = ""; return; }
        try
        {
            var g = TextOutlines.Geometry(info);
            _preview.Data = g;
            var b = g.Bounds;
            if (b.IsEmpty || b.Height <= 0) { _size.Text = "Inga tecken som kan fräsas."; return; }
            _size.Text = P(_height, out var h) && h > 0
                ? $"Storlek: {(h * b.Width / b.Height).ToString("0.#", CultureInfo.InvariantCulture)} × {h.ToString("0.#", CultureInfo.InvariantCulture)} mm"
                : "Ange höjden i mm.";
        }
        catch (Exception ex) { _preview.Data = null; _size.Text = ""; _error.Text = ex.Message; }
    }

    private bool IsInitializedFully => _preview is not null && _size is not null && _align.SelectedItem is not null;

    private void Save()
    {
        var info = Read();
        if (info.Validate() is string problem) { _error.Text = problem; return; }
        if (!P(_height, out var h) || h < TextShapes.MinHeightMm || h > TextShapes.MaxHeightMm)
        { _error.Text = $"Höjden måste vara {TextShapes.MinHeightMm:0}–{TextShapes.MaxHeightMm:0} mm."; return; }
        try { _ = TextOutlines.Shape(info, h, 0, 0); }
        catch (ArgumentException ex) { _error.Text = ex.Message; return; }
        ResultInfo = info; ResultHeight = h;
        DialogResult = true;
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
