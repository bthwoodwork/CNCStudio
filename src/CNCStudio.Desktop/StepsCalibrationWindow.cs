using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Maskiner → (the connected machine) → Kalibrera steg/mm: move one axis a known distance, measure how far it really went, and write
/// the corrected $100/$101/$102. Writing goes through WriteSettingsAsync, so the old settings are saved
/// as a copy first and the value is read back. Repeat to check: the error should then be close to 0.
/// </summary>
public sealed class StepsCalibrationWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Good = B(0x7C, 0xD9, 0x9C), Bad = B(0xFF, 0x7A, 0x86), Warn = B(0xFF, 0xD3, 0x9F);

    private readonly GrblClient _grbl;
    private readonly Func<double, double, double, string?> _checkJog;
    private readonly ComboBox _axis = new() { Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBox _distance = Box(), _feed = Box(), _measured = Box();
    private readonly TextBlock _current = new() { Foreground = Soft, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ink, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Soft, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _forward = new(), _back = new(), _compute = new() { Content = "Räkna ut" }, _write = new() { IsEnabled = false };
    private IReadOnlyDictionary<int, double>? _settings;
    private StepsCalibrationResult? _pending;
    private int _lastDirection;

    public StepsCalibrationWindow(GrblClient grbl, Func<double, double, double, string?> checkJog, string? machineName = null)
    {
        _grbl = grbl; _checkJog = checkJog;
        Title = machineName is null ? "Kalibrera steg/mm" : $"Kalibrera steg/mm – {machineName}";
        Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        foreach (var a in new[] { "X", "Y", "Z" }) _axis.Items.Add(new ComboBoxItem { Content = a });
        _axis.SelectedIndex = 0;
        _axis.SelectionChanged += (_, _) => AxisChanged();
        _forward.Click += async (_, _) => await Move(+1);
        _back.Click += async (_, _) => await Move(-1);
        _compute.Click += (_, _) => Compute();
        _write.Click += async (_, _) => await Write();
        var close = new Button { Content = "Stäng", IsCancel = true };
        close.Click += (_, _) => Close();
        foreach (var b in new[] { _forward, _back, _compute, _write, close })
        {
            b.Padding = new Thickness(12, 6, 12, 6); b.Margin = new Thickness(0, 0, 8, 0); b.Foreground = Brushes.White; b.BorderBrush = B(0x50, 0x67, 0x83);
            b.Background = b == _forward ? B(0x24, 0x61, 0x8C) : b == _write ? B(0x2E, 0x7A, 0x4A) : B(0x34, 0x47, 0x5A);
        }

        Content = new StackPanel { Margin = new Thickness(18), Children =
        {
            new TextBlock { Text = "KALIBRERA STEG/MM", FontSize = 18, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Maskinen flyttar en känd sträcka. Du mäter hur långt den verkligen gick, och CNC Studio räknar ut nytt steg/mm " +
                                   "(nytt = gammalt × beordrat ÷ uppmätt). Ju längre sträcka och noggrannare mätning, desto bättre resultat.",
                Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) },
            Card1(new StackPanel { Children =
            {
                Label("1 · AXEL OCH STRÄCKA"),
                Row(("Axel", _axis), ("Sträcka (mm)", _distance), ("Hastighet (mm/min)", _feed)),
                _current,
            } }),
            Card1(new StackPanel { Children =
            {
                Label("2 · MARKERA OCH FLYTTA"),
                new TextBlock { Text = "Markera var fräsen står: lägg en linjal eller ett skjutmått mot fräsens kant (eller ställ en mätklocka mot vagnen). " +
                                       "Klicka sedan Flytta och mät avståndet till markeringen.", Foreground = Soft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) },
                new WrapPanel { Children = { _forward, _back } },
            } }),
            Card1(new StackPanel { Children =
            {
                Label("3 · MÄT OCH RÄKNA UT"),
                Row(("Uppmätt sträcka (mm)", _measured), (null, null), (null, null)),
                new WrapPanel { Margin = new Thickness(0, 6, 0, 0), Children = { _compute, _write } },
                _result,
            } }),
            _status,
            new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { close } },
        } };
        AxisChanged();
        Loaded += async (_, _) => await ReadSettings();
    }

    private int Axis => _axis.SelectedIndex;

    private void AxisChanged()
    {
        // Sensible starting points: long in X/Y, short in Z (the Z axis has little travel). Both can be changed.
        _distance.Text = Axis == 2 ? "20" : "100";
        _feed.Text = Axis == 2 ? "300" : "1000";
        _measured.Text = "";
        _result.Text = ""; _pending = null; _write.IsEnabled = false; _write.Content = "Skriv till maskinen";
        UpdateButtons();
        ShowCurrent();
    }

    private void UpdateButtons()
    {
        var a = StepsCalibration.AxisName(Axis);
        _forward.Content = $"Flytta {a}+ {_distance.Text} mm";
        _back.Content = $"Flytta tillbaka ({a}−)";
    }

    private void ShowCurrent() =>
        _current.Text = _settings is null ? "Läser maskinens inställningar …"
            : _settings.TryGetValue(100 + Axis, out var v) ? $"Nu: ${100 + Axis} = {v.ToString("0.000", CultureInfo.InvariantCulture)} steg/mm"
            : $"${100 + Axis} saknas i maskinens svar.";

    private async Task ReadSettings()
    {
        try { _settings = await _grbl.ReadSettingsAsync(); ShowCurrent(); }
        catch (Exception ex) { _status.Foreground = Bad; _status.Text = "Kunde inte läsa $$: " + ex.Message; }
    }

    private async Task Move(int direction)
    {
        UpdateButtons();
        if (!P(_distance, out var d) || d <= 0 || !P(_feed, out var f) || f <= 0) { Say("Ange sträcka och hastighet som tal större än 0.", Bad); return; }
        double dx = Axis == 0 ? direction * d : 0, dy = Axis == 1 ? direction * d : 0, dz = Axis == 2 ? direction * d : 0;
        if (_checkJog(dx, dy, dz) is string problem) { Say(problem, Bad); return; }
        _forward.IsEnabled = _back.IsEnabled = false;
        try
        {
            var r = await _grbl.SendLineAsync(StepsCalibration.MoveLine(Axis, direction * d, f));
            if (!r.IsOk) { Say("Maskinen vägrade: " + r.Describe(), Bad); return; }
            Say("Flyttar …", Soft);
            for (int i = 0; i < 600; i++)                      // wait until the move is done (max 2 min)
            {
                await Task.Delay(200);
                var st = await _grbl.RequestStatusAsync(TimeSpan.FromSeconds(1));
                if (st?.State == "Idle") break;
            }
            _lastDirection = direction;
            Say(direction > 0 ? $"Klart – mät hur långt fräsen gick och skriv in det under 3." : "Tillbaka vid startpunkten.", direction > 0 ? Good : Soft);
            if (direction > 0) _measured.Focus();
        }
        catch (Exception ex) { Say("Flytten avbröts: " + ex.Message, Bad); }
        finally { _forward.IsEnabled = _back.IsEnabled = true; }
    }

    private void Compute()
    {
        _pending = null; _write.IsEnabled = false;
        if (_settings is null || !_settings.TryGetValue(100 + Axis, out var old)) { Say("Maskinens steg/mm är inte inläst.", Bad); return; }
        if (!P(_distance, out var d) || !P(_measured, out var m)) { Say("Ange den uppmätta sträckan i mm.", Bad); return; }
        if (StepsCalibration.Validate(old, d, m) is string error) { Say(error, Bad); return; }
        if (_lastDirection != +1) Say("Obs: räkningen förutsätter att du mätte efter en flytt framåt med samma sträcka.", Warn);
        var r = StepsCalibration.Compute(Axis, old, d, m);
        _result.Text = StepsCalibration.Describe(r);
        _result.Foreground = Math.Abs(r.ErrorPercent) < 0.05 ? Good : Ink;
        if (Math.Abs(r.NewStepsPerMm - old) < 0.0005) { _result.Text += "\nIngen ändring behövs."; return; }
        _pending = r;
        _write.Content = $"Skriv ${r.SettingNumber} = {r.NewStepsPerMm.ToString("0.000", CultureInfo.InvariantCulture)}";
        _write.IsEnabled = true;
    }

    private async Task Write()
    {
        if (_pending is not { } r) return;
        if (Math.Abs(r.ErrorPercent) > StepsCalibration.SuspiciousPercent &&
            AppDialog.Show(this, $"Skillnaden är {Math.Abs(r.ErrorPercent):0.#} % – det beror oftast på ett mätfel eller en remskiva som slirar, inte på steg/mm.\n\n" +
                "Mät igen innan du skriver. Skriva ändå?", "Stor skillnad", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        _write.IsEnabled = false;
        try
        {
            var result = await _grbl.WriteSettingsAsync(new[] { (r.SettingNumber, r.NewStepsPerMm) });
            if (!result.Success) { Say("Skrivningen misslyckades: " + result.Message, Bad); return; }
            _settings = result.VerifiedSettings ?? await _grbl.ReadSettingsAsync();
            ShowCurrent();
            _pending = null; _measured.Text = "";
            Say($"${r.SettingNumber} är skrivet och kontrolläst. En kopia av de gamla inställningarna sparades först (Maskiner → $$-kopior). " +
                "Kontrollera: flytta tillbaka, flytta fram igen och mät – skillnaden ska nu vara nära 0. Kör homing efteråt.", Good);
        }
        catch (Exception ex) { Say("Skrivningen avbröts: " + ex.Message, Bad); }
    }

    private void Say(string text, Brush brush) { _status.Text = text; _status.Foreground = brush; }
    private static bool P(TextBox t, out double v) => double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    private static TextBox Box() => new() { Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 2, 0, 0) };
    private static TextBlock Label(string t) => new() { Text = t, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 0, 0, 6) };
    private static Border Card1(UIElement child) => new() { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10), Child = child };

    private static Grid Row(params (string? Label, Control? Box)[] cells)
    {
        var g = new Grid();
        foreach (var _ in cells) g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i].Label is null || cells[i].Box is null) continue;
            var s = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0), Children = { new TextBlock { Text = cells[i].Label, Foreground = Muted, FontSize = 11 }, cells[i].Box! } };
            Grid.SetColumn(s, i); g.Children.Add(s);
        }
        return g;
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
