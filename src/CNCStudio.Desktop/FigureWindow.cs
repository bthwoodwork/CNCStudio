using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Rita → Figurbank…: a ready shape (star, polygon, heart, arrow, ring, rounded rectangle) with a live preview.</summary>
public sealed class FigureWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x8C, 0xA3, 0xBA), Card = B(0x1C, 0x2A, 0x39), Bad = B(0xFF, 0x7A, 0x86);

    public FigureOptions? Result { get; private set; }

    private readonly ListBox _kinds = new() { Background = B(0x10, 0x20, 0x2E), Foreground = Ink, BorderBrush = B(0x3C, 0x51, 0x67), Width = 170 };
    private readonly TextBox _w = Box("80"), _h = Box("80"), _count = Box("5"), _inner = Box("45"), _corner = Box("8");
    private readonly StackPanel _countRow, _innerRow, _cornerRow;
    private readonly Path _preview = new() { Fill = B(0x7F, 0xB8, 0xE8), Stretch = Stretch.Uniform };
    private readonly TextBlock _error = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap };

    public FigureWindow()
    {
        Title = "Figurbank";
        Width = 620; Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        foreach (FigureKind k in Enum.GetValues<FigureKind>()) _kinds.Items.Add(new ListBoxItem { Content = Figures.Name(k), Tag = k, Padding = new Thickness(6, 4, 6, 4) });
        _countRow = Row("Antal sidor / spetsar", _count);
        _innerRow = Row("Inre storlek (%)", _inner);
        _cornerRow = Row("Hörnradie (mm)", _corner);

        var fields = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        fields.Children.Add(new UniformGrid2(Row("Bredd (mm)", _w), Row("Höjd (mm)", _h)));
        fields.Children.Add(_countRow); fields.Children.Add(_innerRow); fields.Children.Add(_cornerRow);
        fields.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(14), Margin = new Thickness(0, 10, 0, 0), Height = 220, Child = _preview });
        fields.Children.Add(_error);

        var ok = new Button { Content = "Lägg till", FontWeight = FontWeights.SemiBold, IsDefault = true };
        var cancel = new Button { Content = "Avbryt", IsCancel = true };
        ok.Click += (_, _) => { if (Read() is { } o && Figures.Validate(o) is null) { Result = o; DialogResult = true; } else Update(); };
        var bottom = new WrapPanel { Margin = new Thickness(14, 8, 14, 14), Children = { ok, cancel } };
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var main = new DockPanel { Margin = new Thickness(14, 14, 14, 0) };
        DockPanel.SetDock(_kinds, Dock.Left); main.Children.Add(_kinds); main.Children.Add(fields);
        root.Children.Add(main);
        Content = root;

        _kinds.SelectionChanged += (_, _) => Update();
        foreach (var b in new[] { _w, _h, _count, _inner, _corner }) b.TextChanged += (_, _) => Update();
        _kinds.SelectedIndex = 2;
    }

    private FigureOptions? Read()
    {
        bool P(TextBox t, out double v) => double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if ((_kinds.SelectedItem as ListBoxItem)?.Tag is not FigureKind k) return null;
        if (!P(_w, out var w) || !P(_h, out var h) || !P(_count, out var n) || !P(_inner, out var inner) || !P(_corner, out var corner)) return null;
        return new FigureOptions { Kind = k, WidthMm = w, HeightMm = h, Count = (int)Math.Round(n), InnerPercent = inner, CornerMm = corner };
    }

    private void Update()
    {
        if (_countRow is null) return;
        var k = (_kinds.SelectedItem as ListBoxItem)?.Tag as FigureKind?;
        _countRow.Visibility = k is FigureKind.Polygon or FigureKind.Star ? Visibility.Visible : Visibility.Collapsed;
        _innerRow.Visibility = k is FigureKind.Star or FigureKind.Ring ? Visibility.Visible : Visibility.Collapsed;
        _cornerRow.Visibility = k is FigureKind.RoundedRectangle ? Visibility.Visible : Visibility.Collapsed;
        var o = Read();
        if (o is null) { _error.Text = "Fyll i talen."; _preview.Data = null; return; }
        _error.Text = Figures.Validate(o) ?? "";
        if (_error.Text.Length > 0) { _preview.Data = null; return; }
        var s = Figures.Create(o, 0, 0);
        var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var c = g.Open())
            foreach (var con in s.Contours)
            {
                c.BeginFigure(new Point(con.Points[0].X * s.Width, con.Points[0].Y * s.Height), true, true);
                c.PolyLineTo(con.Points.Skip(1).Select(p => new Point(p.X * s.Width, p.Y * s.Height)).ToList(), true, false);
            }
        g.Freeze();
        _preview.Data = g;
    }

    private static StackPanel Row(string label, Control c) =>
        new() { Margin = new Thickness(0, 4, 6, 2), Children = { new TextBlock { Text = label, Foreground = Muted, FontSize = 11 }, c } };
    private static TextBox Box(string text) => new() { Text = text, Padding = new Thickness(5, 3, 5, 3) };
    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }

    private sealed class UniformGrid2 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid2(params UIElement[] children) { Columns = 2; foreach (var c in children) Children.Add(c); }
    }
}
