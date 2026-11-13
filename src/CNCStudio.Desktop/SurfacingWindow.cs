using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Maskin → Planfräsa offerskivan… (like gSender's Wasteboard Surfacing): the area, the cutter and the depth
/// in one form. Skapa projekt turns it into an ordinary project (<see cref="WasteboardSurfacing"/>), so the
/// same CAM checks, G-code verification and run apply as for every other project.
/// </summary>
public sealed class SurfacingWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Bad = B(0xFF, 0x7A, 0x86), Good = B(0x7C, 0xD9, 0x9C);

    private readonly MachineProfile _machine;
    private readonly ComboBox _tool = new() { Margin = new Thickness(0, 2, 0, 0), DisplayMemberPath = "DisplayName" };
    private readonly TextBox _diameter = Box(), _stepover = Box(), _feed = Box(), _plunge = Box(), _rpm = Box(),
        _width = Box(), _length = Box(), _fromX = Box(), _fromY = Box(), _thickness = Box(), _depth = Box(), _pass = Box();
    private readonly RadioButton _centred = new() { Content = "Centrerad på bordet", IsChecked = true, Foreground = Ink, Margin = new Thickness(0, 4, 16, 0) },
        _placed = new() { Content = "Från hemhörnet:", Foreground = Ink, Margin = new Thickness(0, 4, 8, 0) };
    private readonly ComboBox _rows = new() { Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _max = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    /// <summary>The surfacing project, set when the user clicks Skapa projekt.</summary>
    public CncDocument? Result { get; private set; }

    public SurfacingWindow(MachineProfile machine, ToolLibrary tools)
    {
        _machine = machine;
        Title = $"Planfräsa offerskivan – {machine.Name}";
        Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        // The tools: facing cutters first, then the widest.
        var list = tools.Tools.Where(t => t.Kind == ToolKind.FlatEndMill)
            .OrderByDescending(t => t.CategoryOrDefault.Contains("plan", StringComparison.CurrentCultureIgnoreCase) || t.Name.Contains("plan", StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(t => t.Diameter).ToList();
        _tool.ItemsSource = list;
        _tool.SelectionChanged += (_, _) => FillTool();
        var d = new SurfacingSettings();
        _diameter.Text = F(d.ToolDiameter); _stepover.Text = F(d.StepoverPercent); _feed.Text = F(d.FeedRate);
        _plunge.Text = F(d.PlungeRate); _rpm.Text = F(d.SpindleRpm);
        _thickness.Text = F(d.WasteboardThickness); _depth.Text = F(d.TotalDepth); _pass.Text = F(d.DepthPerPass);
        _rows.Items.Add("längs X (vänster–höger)"); _rows.Items.Add("längs Y (fram–bak)"); _rows.SelectedIndex = 0;
        _fromX.Text = "0"; _fromY.Text = "0";
        if (list.Count > 0) _tool.SelectedIndex = 0;

        _diameter.TextChanged += (_, _) => ShowMax();
        _centred.Checked += (_, _) => Placement(); _placed.Checked += (_, _) => Placement();
        var whole = Btn("Hela bordet", B(0x34, 0x47, 0x5A));
        whole.ToolTip = "Största ytan maskinen kan planfräsa med den här fräsen";
        whole.Click += (_, _) => { if (Settings(out var s, quiet: true) is { } ok) { var (w, l) = WasteboardSurfacing.MaxArea(_machine, ok); _width.Text = F(w); _length.Text = F(l); _centred.IsChecked = true; } };
        var create = Btn("Skapa projekt", B(0x24, 0x61, 0x8C)); create.IsDefault = true;
        create.Click += (_, _) => Create();
        var cancel = Btn("Avbryt", B(0x34, 0x47, 0x5A)); cancel.IsCancel = true;

        Content = new StackPanel { Margin = new Thickness(18), Children =
        {
            new TextBlock { Text = "PLANFRÄSA OFFERSKIVAN", FontSize = 18, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Fräser offerskivan plan, rad för rad, med en bred fräs. Det blir ett vanligt projekt med planfräsning, så samma kontroller och samma körning gäller som annars – " +
                                   "och du kan spara det och köra det igen nästa gång. X0 Y0 är ytans främre vänstra hörn och Z0 offerskivans ovansida.",
                Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) },
            Card1(new StackPanel { Children =
            {
                Label("1 · FRÄS"),
                Row(("Fräs ur verktygsbiblioteket", _tool)),
                Row(("Diameter (mm)", _diameter), ("Stegning (% av diametern)", _stepover), ("Rader", _rows)),
                Row(("Matning (mm/min)", _feed), ("Nedmatning (mm/min)", _plunge), ("Varvtal (varv/min)", _rpm)),
            } }),
            Card1(new StackPanel { Children =
            {
                Label("2 · YTA"),
                Row(("Bredd X (mm)", _width), ("Djup Y (mm)", _length), ("Offerskivans tjocklek (mm)", _thickness)),
                new WrapPanel { Children = { whole } },
                new WrapPanel { Margin = new Thickness(0, 6, 0, 0), Children = { _centred, _placed,
                    Small("X"), _fromX, Small("Y"), _fromY } },
                _max,
            } }),
            Card1(new StackPanel { Children =
            {
                Label("3 · DJUP"),
                Row(("Totalt djup (mm)", _depth), ("Djup per pass (mm)", _pass)),
                new TextBlock { Text = "Ta så lite som behövs för att få bort ojämnheterna – ofta räcker 0,3–0,5 mm.", Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) },
            } }),
            _status,
            new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { create, cancel } },
        } };
        _fromX.Width = _fromY.Width = 70;
        Placement();
        if (Settings(out _, quiet: true) is { } s0) { var (w, l) = WasteboardSurfacing.MaxArea(_machine, s0); _width.Text = F(w); _length.Text = F(l); }
        ShowMax();
        if (list.Count == 0) Say("Verktygsbiblioteket har inga raka fräsar – fyll i fräsens diameter och skärdata för hand.", Muted);
    }

    private void FillTool()
    {
        if (_tool.SelectedItem is not ToolDefinition t) return;
        var v = t.CuttingFor(null);
        _diameter.Text = F(t.Diameter); _feed.Text = F(v.FeedRate); _plunge.Text = F(v.PlungeRate); _rpm.Text = F(v.SpindleRpm);
        _pass.Text = F(Math.Min(v.DepthPerPass, 0.5));
        ShowMax();
    }

    private void Placement() => _fromX.IsEnabled = _fromY.IsEnabled = _placed.IsChecked == true;

    private void ShowMax()
    {
        if (_max is null) return;
        if (Settings(out _, quiet: true) is not { } s) { _max.Text = ""; return; }
        var (w, l) = WasteboardSurfacing.MaxArea(_machine, s);
        _max.Text = $"Maskinen är {F(_machine.WorkWidthMm)} × {F(_machine.WorkLengthMm)} mm. Med {F(s.ToolDiameter)} mm fräs kan högst {F(w)} × {F(l)} mm planfräsas: " +
                    $"raderna går ut förbi kanten tills hela fräsen har lämnat ytan, och {F(_machine.LimitMarginMm)} mm ska finnas kvar till gränslägena.";
    }

    /// <summary>The settings from the fields; null (with the reason shown unless <paramref name="quiet"/>) when a value is not a number.</summary>
    private SurfacingSettings? Settings(out string? error, bool quiet = false)
    {
        error = null;
        // & (not &&): every field is read, so each value is set whatever the others hold.
        bool numbers = P(_diameter, out var dia) & P(_stepover, out var step) & P(_feed, out var feed) & P(_plunge, out var plunge) &
                       P(_rpm, out var rpm) & P(_thickness, out var thick) & P(_depth, out var depth) & P(_pass, out var pass);
        if (!numbers) error = "Fyll i alla fält med tal.";
        double? w = null, l = null, fx = null, fy = null;
        if (error is null && P(_width, out var wv)) w = wv; else if (error is null && !string.IsNullOrWhiteSpace(_width.Text)) error = "Ange ytans bredd i mm.";
        if (error is null && P(_length, out var lv)) l = lv; else if (error is null && !string.IsNullOrWhiteSpace(_length.Text)) error = "Ange ytans djup i mm.";
        if (error is null && _placed.IsChecked == true)
        {
            if (P(_fromX, out var x) && P(_fromY, out var y)) { fx = x; fy = y; }
            else error = "Ange var ytans närmaste hörn ligger från hemhörnet (X och Y i mm).";
        }
        if (error is not null) { if (!quiet) Say(error, Bad); return null; }
        var tool = _tool.SelectedItem as ToolDefinition;
        return new SurfacingSettings
        {
            Width = w, Length = l, FromHomeX = fx, FromHomeY = fy, WasteboardThickness = thick, TotalDepth = depth, DepthPerPass = pass,
            ToolName = tool is not null && Math.Abs(tool.Diameter - dia) < 1e-9 ? tool.Name : $"{F(dia)} mm planfräs",
            ToolDiameter = dia, StepoverPercent = step, FeedRate = feed, PlungeRate = plunge, SpindleRpm = rpm, RowsAlongY = _rows.SelectedIndex == 1
        };
    }

    private void Create()
    {
        if (Settings(out _) is not { } s) return;
        if (WasteboardSurfacing.Validate(_machine, s) is string error) { Say(error, Bad); return; }
        try { Result = WasteboardSurfacing.CreateProject(_machine, s); }
        catch (Exception ex) { Say(ex.Message, Bad); return; }
        DialogResult = true;
    }

    private void Say(string text, Brush brush) { _status.Text = text; _status.Foreground = brush; }
    private static bool P(TextBox t, out double v) =>
        double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static TextBox Box() => new() { Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 2, 0, 0) };
    private static TextBlock Label(string t) => new() { Text = t, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 0, 0, 6) };
    private static TextBlock Small(string t) => new() { Text = t, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 4, 4, 0) };
    private static Border Card1(UIElement child) => new() { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10), Child = child };

    private static Button Btn(string text, Brush background) => new()
    {
        Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 6, 8, 0),
        Foreground = Brushes.White, Background = background, BorderBrush = B(0x50, 0x67, 0x83)
    };

    private static Grid Row(params (string Label, Control Box)[] cells)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var _ in cells) g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < cells.Length; i++)
        {
            var s = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0), Children = { new TextBlock { Text = cells[i].Label, Foreground = Muted, FontSize = 11 }, cells[i].Box } };
            Grid.SetColumn(s, i); g.Children.Add(s);
        }
        return g;
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
