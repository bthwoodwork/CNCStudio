using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Rita → Låda med fingerskarvar…: the box's measures, fingers, clearance, bottom and cutter, with what it gives.</summary>
public sealed class BoxWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x9F, 0xB3, 0xC8), Bad = B(0xFF, 0x7A, 0x86), Card = B(0x1C, 0x2A, 0x39);

    public BoxOptions? Result { get; private set; }
    public ToolDefinition? Tool => _tool.SelectedItem as ToolDefinition;

    private readonly TextBox _l = Num("200"), _w = Num("120"), _h = Num("80"), _finger = Num("20"), _clear = Num("0.1"), _dia = Num("6");
    private readonly ComboBox _measure = new() { Margin = new Thickness(3), SelectedIndex = 0 };
    private readonly CheckBox _bottom = new() { Content = "Botten (fingerskarvad)", IsChecked = true, Foreground = Ink, Margin = new Thickness(3, 8, 3, 3) };
    private readonly ComboBox _tool = new() { Margin = new Thickness(3), DisplayMemberPath = "DisplayName" };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ink };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Bad, Margin = new Thickness(0, 6, 0, 0) };
    private readonly double _thickness;

    public BoxWindow(double thickness, IEnumerable<ToolDefinition> tools, ToolDefinition? preferred)
    {
        _thickness = thickness;
        Title = "Låda med fingerskarvar";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        _measure.Items.Add(new ComboBoxItem { Content = "Yttermått" });
        _measure.Items.Add(new ComboBoxItem { Content = "Innermått" });
        foreach (var t in tools.Where(t => t.Kind == ToolKind.FlatEndMill)) _tool.Items.Add(t);
        _tool.SelectedItem = preferred is { Kind: ToolKind.FlatEndMill } && _tool.Items.Contains(preferred) ? preferred : _tool.Items.Cast<ToolDefinition>().FirstOrDefault(t => Math.Abs(t.Diameter - 6) < 0.6) ?? (_tool.Items.Count > 0 ? _tool.Items[0] : null);
        if (Tool is { } st) _dia.Text = st.Diameter.ToString("0.###", CultureInfo.InvariantCulture);
        _tool.SelectionChanged += (_, _) => { if (Tool is { } t) _dia.Text = t.Diameter.ToString("0.###", CultureInfo.InvariantCulture); Update(); };

        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        root.Children.Add(new TextBlock
        {
            Text = $"Fyra sidor och en botten med fingerskarvar, i projektets skiva ({thickness:0.##} mm). Delarna läggs ut på materialet med utskärning, hållflikar och dogbones – så att fingrarna går i botten av urtagen.",
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 10)
        });
        var g = new Grid();
        for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
        g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition());
        Add(g, "Längd (mm)", _l, 0, 0); Add(g, "Bredd (mm)", _w, 1, 0); Add(g, "Höjd (mm)", _h, 2, 0);
        Add(g, "Måtten är", _measure, 0, 1); Add(g, "Fingerbredd ca (mm)", _finger, 1, 1); Add(g, "Spel för lim (mm)", _clear, 2, 1);
        root.Children.Add(g);
        root.Children.Add(_bottom);
        root.Children.Add(new TextBlock { Text = "Fräs för utskärningen", Foreground = Muted, FontSize = 11, Margin = new Thickness(3, 8, 3, 0) });
        var toolRow = new DockPanel();
        var diaBox = Labeled("Ø (mm)", _dia); diaBox.Width = 90; DockPanel.SetDock(diaBox, Dock.Right);
        toolRow.Children.Add(diaBox); toolRow.Children.Add(_tool);
        root.Children.Add(toolRow);
        root.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 10, 0, 0), Child = _info });
        root.Children.Add(_error);
        var ok = new Button { Content = "Skapa lådan", FontWeight = FontWeights.SemiBold, Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        var cancel = new Button { Content = "Avbryt", IsCancel = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => { if (Read() is { } o && BoxJoints.Validate(o) is null) { Result = o; DialogResult = true; } else Update(); };
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } });
        Content = root;

        foreach (var box in new[] { _l, _w, _h, _finger, _clear, _dia }) box.TextChanged += (_, _) => Update();
        _measure.SelectionChanged += (_, _) => Update();
        _bottom.Checked += (_, _) => Update(); _bottom.Unchecked += (_, _) => Update();
        Update();
    }

    private BoxOptions? Read()
    {
        static bool P(TextBox b, out double v) => double.TryParse(b.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (!P(_l, out var l) || !P(_w, out var w) || !P(_h, out var h) || !P(_finger, out var f) || !P(_clear, out var c) || !P(_dia, out var d))
        { _error.Text = "Fyll i alla mått som tal."; return null; }
        return new BoxOptions { LengthMm = l, WidthMm = w, HeightMm = h, FingerWidthMm = f, ClearanceMm = c, ToolDiameterMm = d,
            InsideMeasures = _measure.SelectedIndex == 1, Bottom = _bottom.IsChecked == true, ThicknessMm = _thickness };
    }

    private void Update()
    {
        if (_info is null) return;
        var o = Read();
        if (o is null) { _info.Text = ""; return; }
        if (BoxJoints.Validate(o) is string problem) { _error.Text = problem; _info.Text = ""; return; }
        _error.Text = "";
        var (l, w, h) = BoxJoints.Outer(o);
        string Edge(double e) { int n = BoxJoints.Fingers(e, o.FingerWidthMm); return $"{n} fingrar à {e / n:0.#} mm"; }
        _info.Text = $"Yttermått {l:0.#} × {w:0.#} × {h:0.#} mm, inne {l - 2 * o.ThicknessMm:0.#} × {w - 2 * o.ThicknessMm:0.#} × {h - (o.Bottom ? o.ThicknessMm : 0):0.#} mm.\n" +
                     $"Hörnen: {Edge(h)}." + (o.Bottom ? $" Botten: längs {Edge(l)}, tvärs {Edge(w)}." : " Ingen botten.") +
                     $"\n{(o.Bottom ? 5 : 4)} delar.";
    }

    private static void Add(Grid g, string label, Control field, int col, int row)
    {
        var f = Labeled(label, field);
        Grid.SetColumn(f, col); Grid.SetRow(f, row);
        g.Children.Add(f);
    }

    private static StackPanel Labeled(string label, Control field) =>
        new() { Children = { new TextBlock { Text = label, Foreground = Muted, FontSize = 11, Margin = new Thickness(3, 4, 3, 0) }, field } };

    private static TextBox Num(string t) => new() { Text = t, Margin = new Thickness(3), Padding = new Thickness(4, 2, 4, 2) };
    private static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}
