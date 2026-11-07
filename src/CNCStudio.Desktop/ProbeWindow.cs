using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Probing with a touch plate (Maskinkontroll → Probning…): Z (default), corner X/Y/Z, X/Y, X or Y.
/// Every value is a setting, saved for all computers. When Z is probed, the stock top (MPos Z) is
/// handed back so the Z checks – and the next program of a multi-tool job – use it.
/// </summary>
public sealed class ProbeWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Bad = B(0xFF, 0x7A, 0x86), Good = B(0x7C, 0xD9, 0x9C), Warn = B(0xFF, 0xD3, 0x9F);

    private readonly GrblClient _grbl;
    private readonly ProbeSettings _s;
    private readonly Action<ProbeResult> _onDone;
    private readonly bool _projectZeroIsCentre;

    private readonly ComboBox _kind = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBlock _how = new() { TextWrapping = TextWrapping.Wrap, Foreground = Soft, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _warn = new() { TextWrapping = TextWrapping.Wrap, Foreground = Warn, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _tool = Field("6");
    private readonly StackPanel _toolRow;
    private readonly TextBox _thick = Field(""), _wallX = Field(""), _wallY = Field(""), _fast = Field(""), _slow = Field(""),
        _maxZ = Field(""), _maxXY = Field(""), _retract = Field(""), _out = Field(""), _depth = Field(""), _lift = Field("");
    private readonly TextBlock _log = new() { TextWrapping = TextWrapping.Wrap, Foreground = Soft, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly Button _start = new() { Content = "▶ Starta probning", FontWeight = FontWeights.SemiBold };
    private readonly Button _test = new() { Content = "Testa kontakt" };
    private CancellationTokenSource? _cts;

    public ProbeWindow(GrblClient grbl, ProbeSettings settings, double toolDiameter, bool projectZeroIsCentre, Action<ProbeResult> onDone)
    {
        _grbl = grbl; _s = settings; _onDone = onDone; _projectZeroIsCentre = projectZeroIsCentre;
        Title = "Probning";
        Width = 520; SizeToContent = SizeToContent.Height; MaxHeight = 900; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        foreach (var (text, kind) in new[] { ("Z – materialytan (vanligast)", ProbeKind.Z), ("Hörn X, Y och Z", ProbeKind.XYZ),
                     ("Hörn X och Y", ProbeKind.XY), ("Bara X", ProbeKind.X), ("Bara Y", ProbeKind.Y) })
            _kind.Items.Add(new ComboBoxItem { Content = text, Tag = kind });
        _kind.SelectedIndex = 0;                                   // Z is the default
        _kind.SelectionChanged += (_, _) => UpdateKind();
        _tool.Text = toolDiameter.ToString("0.###", CultureInfo.InvariantCulture);
        _toolRow = Row(("Fräsens diameter (mm)", _tool, "Behövs för X/Y: kanten ligger fräsradien längre in än kontaktpunkten."));

        _thick.Text = F(_s.PlateThicknessMm); _wallX.Text = F(_s.PlateWallXMm); _wallY.Text = F(_s.PlateWallYMm);
        _fast.Text = F(_s.FastFeed); _slow.Text = F(_s.SlowFeed); _maxZ.Text = F(_s.MaxTravelZMm); _maxXY.Text = F(_s.MaxTravelXYMm);
        _retract.Text = F(_s.RetractMm); _out.Text = F(_s.SideMoveOutMm); _depth.Text = F(_s.SideDepthMm); _lift.Text = F(_s.FinalLiftMm);

        var plate = new Expander
        {
            Header = new TextBlock { Text = "PLATTANS MÅTT OCH HASTIGHETER", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = B(0xA5, 0xC0, 0xD9) },
            IsExpanded = false, Margin = new Thickness(0, 4, 0, 8),
            Content = new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 0), Child = new StackPanel { Children =
            {
                Row(("Plattans tjocklek (mm)", _thick, "Plattans ovansida ligger så här högt över materialet."),
                    ("Lyft efteråt (mm)", _lift, "Höjd över materialet när probningen är klar.")),
                Row(("Vägg X (mm)", _wallX, "Hörnplatta: från plattans vänstra yttersida till materialets kant."),
                    ("Vägg Y (mm)", _wallY, "Hörnplatta: från plattans främre yttersida till materialets kant.")),
                Row(("Snabb sökning (mm/min)", _fast, "Första sökningen."), ("Långsam (mm/min)", _slow, "Andra, mätande beröringen.")),
                Row(("Sök Z högst (mm)", _maxZ, "Ingen kontakt inom detta → stopp (ALARM:5)."), ("Sök X/Y högst (mm)", _maxXY, "Extra sökväg in mot väggen.")),
                Row(("Backa (mm)", _retract, "Mellan snabb och långsam beröring."), ("Ut åt sidan (mm)", _out, "X/Y: så långt ut från startpunkten innan fräsen går ned bredvid plattan.")),
                Row(("Sidodjup (mm)", _depth, "X/Y efter Z: så långt under plattans ovansida fräsen rör väggen."), (null, null, null)),
            } } }
        };

        _test.Click += async (_, _) => await TestContact();
        _start.Click += async (_, _) => await Run();
        var close = new Button { Content = "Stäng", IsCancel = true };
        close.Click += (_, _) => { _cts?.Cancel(); Close(); };
        foreach (var b in new[] { _start, _test, close })
        {
            b.Padding = new Thickness(14, 7, 14, 7); b.Margin = new Thickness(0, 0, 8, 0); b.Foreground = Brushes.White;
            b.Background = b == _start ? B(0x2E, 0x7A, 0x4A) : B(0x34, 0x47, 0x5A); b.BorderBrush = B(0x50, 0x67, 0x83);
        }

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Margin = new Thickness(18), Children =
        {
            new TextBlock { Text = "PROBNING", FontSize = 18, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Vad ska probas?", Foreground = Soft, Margin = new Thickness(0, 10, 0, 0) },
            _kind, _how, _warn, _toolRow, plate,
            new WrapPanel { Margin = new Thickness(0, 6, 0, 10), Children = { _start, _test, close } },
            new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), MinHeight = 60, Child = _log }
        } } };
        UpdateKind();
        _log.Text = "Sätt klämman på fräsen och lägg plattan enligt beskrivningen ovan.";
    }

    private ProbeKind Kind => (ProbeKind)((ComboBoxItem)_kind.SelectedItem).Tag;

    private void UpdateKind()
    {
        _how.Text = Kind switch
        {
            ProbeKind.Z => "Lägg plattan på materialet och ställ fräsen några mm ovanför plattan. Fräsen söker nedåt, rör plattan två gånger (snabbt, sedan långsamt) och sätter Z0 på materialytan.",
            ProbeKind.XYZ => "Lägg hörnplattan i materialets främre vänstra hörn och ställ fräsen över plattan, nära hörnet. Först Z på ovansidan, sedan går fräsen ut till vänster och fram och rör plattans sidor. X0 Y0 hamnar i materialets hörn.",
            ProbeKind.XY => "Lägg hörnplattan i materialets främre vänstra hörn. Ställ fräsen bredvid plattan i höjd med dess sidor, innanför hörnet. Fräsen går ut åt vänster och fram och rör sidorna. Z ändras inte.",
            ProbeKind.X => "Ställ fräsen i höjd med plattans sida, innanför plattans vänstra kant. Fräsen går ut åt vänster och söker sedan in mot sidan. Bara X sätts.",
            _ => "Ställ fräsen i höjd med plattans sida, innanför plattans främre kant. Fräsen går ut framåt och söker sedan in mot sidan. Bara Y sätts."
        };
        _toolRow.Visibility = Kind == ProbeKind.Z ? Visibility.Collapsed : Visibility.Visible;
        _warn.Text = Kind != ProbeKind.Z && _projectZeroIsCentre
            ? "Obs: projektets nollpunkt X/Y är materialets MITT. Hörnprobning sätter X0 Y0 i materialets främre vänstra hörn."
            : "";
        _warn.Visibility = _warn.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Waits for the probe input (Pn:P) while the user touches the plate to the tool.</summary>
    private async Task TestContact()
    {
        _test.IsEnabled = false;
        _log.Foreground = Soft;
        _log.Text = "Rör plattan mot fräsen nu (inom 10 sekunder) …";
        try
        {
            for (int i = 0; i < 40; i++)
            {
                var st = await _grbl.RequestStatusAsync(TimeSpan.FromSeconds(1));
                if (st?.Pins?.Contains('P') == true)
                { _log.Foreground = Good; _log.Text = "Kontakt! Proben fungerar. Släpp plattan och lägg den på plats."; return; }
                await Task.Delay(250);
            }
            _log.Foreground = Bad;
            _log.Text = "Ingen kontakt kändes. Kontrollera kabeln, klämman och att fräsen leder ström (ingen färg/lack på skaftet).";
        }
        catch (Exception ex) { _log.Foreground = Bad; _log.Text = "Kunde inte läsa proben: " + ex.Message; }
        finally { _test.IsEnabled = true; }
    }

    private async Task Run()
    {
        if (!ReadSettings(out var tool)) return;
        if (ProbeValidation.GetError(_s, Kind, tool) is string error) { _log.Foreground = Bad; _log.Text = error; return; }
        try { AppStorage.Current.SaveProbeSettings(_s); } catch (Exception) { /* the probe still runs with the values */ }
        _start.IsEnabled = false; _test.IsEnabled = false; _kind.IsEnabled = false;
        _log.Foreground = Soft; _log.Text = "";
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(line => _log.Text += (_log.Text.Length > 0 ? "\n" : "") + line);
        try
        {
            var result = await new ProbeCycle(_grbl, _s).RunAsync(Kind, tool, progress, _cts.Token);
            _log.Text += (_log.Text.Length > 0 ? "\n\n" : "") + result.Message;
            _log.Foreground = result.Success ? Good : Bad;
            if (result.Success) _onDone(result);
        }
        catch (Exception ex) { _log.Foreground = Bad; _log.Text += "\n" + ex.Message; }
        finally { _start.IsEnabled = true; _test.IsEnabled = true; _kind.IsEnabled = true; }
    }

    private bool ReadSettings(out double tool)
    {
        tool = 0;
        bool P(TextBox t, out double v) => double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (!P(_tool, out tool) && Kind != ProbeKind.Z) { _log.Foreground = Bad; _log.Text = "Ange fräsens diameter i mm."; return false; }
        var fields = new (TextBox Box, Action<double> Set)[]
        {
            (_thick, v => _s.PlateThicknessMm = v), (_wallX, v => _s.PlateWallXMm = v), (_wallY, v => _s.PlateWallYMm = v),
            (_fast, v => _s.FastFeed = v), (_slow, v => _s.SlowFeed = v), (_maxZ, v => _s.MaxTravelZMm = v), (_maxXY, v => _s.MaxTravelXYMm = v),
            (_retract, v => _s.RetractMm = v), (_out, v => _s.SideMoveOutMm = v), (_depth, v => _s.SideDepthMm = v), (_lift, v => _s.FinalLiftMm = v)
        };
        foreach (var (box, set) in fields)
        {
            if (!P(box, out var v)) { _log.Foreground = Bad; _log.Text = "Alla mått och hastigheter måste vara tal (öppna Plattans mått och hastigheter)."; box.Focus(); return false; }
            set(v);
        }
        return true;
    }

    private static StackPanel Row(params (string? Label, TextBox? Box, string? Tip)[] cells)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < 2; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < cells.Length; i++)
        {
            var (label, box, tip) = cells[i];
            if (label is null || box is null) continue;
            box.ToolTip = tip;
            var cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 6, 0, i == 0 ? 6 : 0, 0), Children =
                { new TextBlock { Text = label, Foreground = Muted, FontSize = 11, ToolTip = tip }, box } };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return new StackPanel { Children = { grid } };
    }

    private static TextBox Field(string text) => new() { Text = text, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 2, 0, 0) };
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
