using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>CAM → Skapa inlay: the V-bit, the pocket's and the plug's depths and the plug board, with what the fit gives.</summary>
public sealed class InlayWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x8C, 0xA3, 0xBA), Card = B(0x1C, 0x2A, 0x39),
        Good = B(0x7C, 0xD9, 0x9C), Bad = B(0xFF, 0x7A, 0x86);

    public InlayPlan? Result { get; private set; }

    private readonly double _baseThickness;
    private readonly Guid? _materialId;
    private readonly ComboBox _tool = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _angle = Box(), _dia = Box(), _pocket = Box(), _start = Box(), _plug = Box(), _clear = Box(), _board = Box(), _pass = Box();
    private readonly TextBlock _explain = new() { Foreground = Ink, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap };
    private double _feed = 1200, _plunge = 300, _rpm = 18000;
    private string? _toolName;

    public InlayWindow(IEnumerable<ToolDefinition> tools, double baseThickness, Guid? materialId, string what)
    {
        _baseThickness = baseThickness; _materialId = materialId;
        Title = "Skapa inlay";
        Width = 560; Height = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        var vbits = tools.Where(t => t.Kind == ToolKind.VBit).ToList();
        foreach (var t in vbits) _tool.Items.Add(new ComboBoxItem { Content = $"{t.Name} ({F(t.AngleDeg)}°, Ø {F(t.Diameter)} mm)", Tag = t });
        _tool.Items.Add(new ComboBoxItem { Content = "Egen V-fräs (ange vinkel och diameter)", Tag = null });
        _tool.SelectionChanged += (_, _) => ToolChosen();

        var d = new InlayPlan { PlugBoardThicknessMm = baseThickness };
        _angle.Text = F(d.AngleDeg); _dia.Text = F(d.ToolDiameter); _pocket.Text = F(d.PocketDepthMm); _start.Text = F(d.PlugStartDepthMm);
        _plug.Text = F(d.PlugDepthMm); _clear.Text = F(d.ClearanceMm); _board.Text = F(d.PlugBoardThicknessMm); _pass.Text = F(d.DepthPerPass);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 6) };
        body.Children.Add(new TextBlock
        {
            Text = $"Inlay av {what}. Honan V-carvas i det här projektet. Hanen blir ett eget projekt, speglat, att fräsa i en annan skiva, vända och limma i honan.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        body.Children.Add(Head("V-FRÄS"));
        body.Children.Add(_tool);
        body.Children.Add(Pair("Vinkel (grader)", _angle, "Diameter (mm)", _dia));
        body.Children.Add(Head("HONA (I DET HÄR MATERIALET)"));
        body.Children.Add(Pair("Honans djup (mm)", _pocket, "Djup per pass (mm)", _pass));
        body.Children.Add(Head("HANE (EGEN SKIVA)"));
        body.Children.Add(Pair("Startdjup vid konturen (mm)", _start, "Hanens djup (mm)", _plug));
        body.Children.Add(Pair("Spel runt om (mm)", _clear, "Hanens skiva, tjocklek (mm)", _board));
        body.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0), Child = _explain });

        var ok = new Button { Content = "Skapa hona och hane", FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Avbryt", IsCancel = true };
        ok.Click += (_, _) => Save();
        var buttons = new WrapPanel { Margin = new Thickness(18, 6, 18, 14), Children = { ok, cancel } };
        var bottom = new StackPanel { Children = { new Border { Padding = new Thickness(18, 0, 18, 0), Child = _error }, buttons } };
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = root;

        foreach (var b in new[] { _angle, _dia, _pocket, _start, _plug, _clear, _board, _pass }) b.TextChanged += (_, _) => Update();
        _tool.SelectedIndex = 0;
        Update();
    }

    private void ToolChosen()
    {
        if ((_tool.SelectedItem as ComboBoxItem)?.Tag is not ToolDefinition t) { _toolName = null; _angle.IsEnabled = _dia.IsEnabled = true; Update(); return; }
        var v = t.CuttingFor(_materialId);
        _toolName = t.Name;
        _angle.Text = F(t.AngleDeg); _dia.Text = F(t.Diameter);
        _feed = v.FeedRate; _plunge = v.PlungeRate; _rpm = v.SpindleRpm; _pass.Text = F(v.DepthPerPass);
        _angle.IsEnabled = _dia.IsEnabled = false;
        Update();
    }

    private InlayPlan? Read()
    {
        bool P(TextBox t, out double v) => double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (!P(_angle, out var a) || !P(_dia, out var dia) || !P(_pocket, out var pocket) || !P(_start, out var start) || !P(_plug, out var plug) ||
            !P(_clear, out var clear) || !P(_board, out var board) || !P(_pass, out var pass)) return null;
        return new InlayPlan
        {
            AngleDeg = a, ToolDiameter = dia, ToolName = _toolName, PocketDepthMm = pocket, PlugStartDepthMm = start, PlugDepthMm = plug,
            ClearanceMm = clear, PlugBoardThicknessMm = board, DepthPerPass = pass, FeedRate = _feed, PlungeRate = _plunge, SpindleRpm = _rpm,
        };
    }

    private void Update()
    {
        var p = Read();
        if (p is null) { _error.Text = "Fyll i alla fält med tal."; _explain.Text = ""; return; }
        _error.Text = p.Validate(_baseThickness) ?? "";
        _explain.Foreground = _error.Text.Length == 0 ? Good : Ink;
        _explain.Text =
            $"• Glipa i botten för lim: {F(p.BottomGapMm)} mm (honans djup − hanens startdjup).\n" +
            $"• Hanen står {F(p.ProtrusionMm)} mm över honans yta när den sitter i – såga eller plana bort det när limmet har torkat.\n" +
            $"• Spel på väggarna: {F(p.ClearanceMm)} mm runt om.\n" +
            $"• V-fräsen är full bredd på {F(Math.Round(p.ToolMaxDepth, 2))} mm djup – breda partier får plan botten på honans djup.\n" +
            "Vanligt i trä: honan 4–5 mm, startdjupet 1 mm grundare, hanen 1–2 mm djupare än startdjupet.";
    }

    private void Save()
    {
        var p = Read();
        if (p is null) { _error.Text = "Fyll i alla fält med tal."; return; }
        if (p.Validate(_baseThickness) is string e) { _error.Text = e; return; }
        Result = p;
        DialogResult = true;
    }

    private static TextBlock Head(string t) => new() { Text = t, Foreground = Muted, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) };
    private static TextBox Box() => new() { Padding = new Thickness(5, 3, 5, 3) };
    private static FrameworkElement Pair(string a, Control ca, string b, Control cb)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); g.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Children = { new TextBlock { Text = a, Foreground = Muted, FontSize = 11 }, ca } };
        var right = new StackPanel { Children = { new TextBlock { Text = b, Foreground = Muted, FontSize = 11 }, cb } };
        Grid.SetColumn(right, 2);
        g.Children.Add(left); g.Children.Add(right);
        return g;
    }
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
