using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Probing (modal): pick the type – Z, X, Y, XY or XYZ – with large buttons, the corner for X/Y types, see a
/// sketch of where the plate and the cutter go, and set only the values that type uses (speeds under
/// Avancerat). Last type and corner are remembered. When Z is probed, the stock top is handed back so the
/// Z checks – and the next program of a multi-tool job – use it.
/// </summary>
public sealed class ProbeWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), TileOff = B(0x26, 0x38, 0x4D), TileOn = B(0x24, 0x61, 0x8C), Bad = B(0xFF, 0x7A, 0x86),
        Good = B(0x7C, 0xD9, 0x9C), Warn = B(0xFF, 0xD3, 0x9F), Wood = B(0xC9, 0xA5, 0x74), Plate = B(0x9A, 0xA8, 0xB8), Arrow = B(0xFF, 0x9F, 0x3A);

    private readonly GrblClient _grbl;
    private readonly ProbeSettings _s;
    private readonly Action<ProbeResult> _onDone;
    private readonly bool _projectZeroIsCentre;
    private ProbeKind _kind;
    private ProbeCorner _corner;

    private readonly Dictionary<ProbeKind, Button> _tiles = new();
    private readonly Dictionary<ProbeCorner, Button> _cornerButtons = new();
    private readonly StackPanel _cornerRow = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly Canvas _sketch = new() { Width = 300, Height = 210, ClipToBounds = true };
    private readonly TextBlock _how = new() { TextWrapping = TextWrapping.Wrap, Foreground = Soft, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _warn = new() { TextWrapping = TextWrapping.Wrap, Foreground = Warn, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _fields = new();
    private readonly TextBox _tool = Field(), _thick = Field(), _wallX = Field(), _wallY = Field(), _fast = Field(), _slow = Field(),
        _maxZ = Field(), _maxXY = Field(), _retract = Field(), _out = Field(), _depth = Field(), _lift = Field();
    private readonly TextBlock _log = new() { TextWrapping = TextWrapping.Wrap, Foreground = Soft, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly Button _start = new() { Content = "▶  Starta probning", FontWeight = FontWeights.SemiBold, FontSize = 14 };
    private readonly Button _test = new() { Content = "Testa kontakt" };
    private readonly Button _close = new() { Content = "Stäng" };
    private bool _running;

    public ProbeWindow(GrblClient grbl, ProbeSettings settings, double toolDiameter, bool projectZeroIsCentre, Action<ProbeResult> onDone)
    {
        _grbl = grbl; _s = settings; _onDone = onDone; _projectZeroIsCentre = projectZeroIsCentre;
        _kind = settings.LastKind; _corner = settings.Corner;
        Title = "Probning";
        Width = 900; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        _tool.Text = F(toolDiameter);
        _thick.Text = F(_s.PlateThicknessMm); _wallX.Text = F(_s.PlateWallXMm); _wallY.Text = F(_s.PlateWallYMm);
        _fast.Text = F(_s.FastFeed); _slow.Text = F(_s.SlowFeed); _maxZ.Text = F(_s.MaxTravelZMm); _maxXY.Text = F(_s.MaxTravelXYMm);
        _retract.Text = F(_s.RetractMm); _out.Text = F(_s.SideMoveOutMm); _depth.Text = F(_s.SideDepthMm); _lift.Text = F(_s.FinalLiftMm);

        // Type tiles.
        var tiles = new UniformGrid { Columns = 5, Margin = new Thickness(0, 4, 0, 10) };
        foreach (var (kind, title, sub) in new[]
                 {
                     (ProbeKind.Z, "Z", "materialytan"), (ProbeKind.X, "X", "en kant"), (ProbeKind.Y, "Y", "en kant"),
                     (ProbeKind.XY, "XY", "hörn"), (ProbeKind.XYZ, "XYZ", "hörn + yta"),
                 })
        {
            var b = new Button
            {
                Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 10, 8, 10), Cursor = Cursors.Hand, Foreground = Ink,
                BorderBrush = B(0x4A, 0x60, 0x78),
                Content = new StackPanel { Children =
                {
                    new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = sub, FontSize = 11.5, Foreground = Soft, HorizontalAlignment = HorizontalAlignment.Center }
                } }
            };
            var k = kind;
            b.Click += (_, _) => { if (!_running) { _kind = k; Refresh(); } };
            _tiles[kind] = b;
            tiles.Children.Add(b);
        }

        // Corner picker (X/Y types).
        var corners = new Grid { Width = 230, HorizontalAlignment = HorizontalAlignment.Left };
        corners.ColumnDefinitions.Add(new ColumnDefinition()); corners.ColumnDefinitions.Add(new ColumnDefinition());
        corners.RowDefinitions.Add(new RowDefinition()); corners.RowDefinitions.Add(new RowDefinition());
        foreach (var (c, text, row, col) in new[] { (ProbeCorner.BackLeft, "↖ Bak vänster", 0, 0), (ProbeCorner.BackRight, "Bak höger ↗", 0, 1),
                     (ProbeCorner.FrontLeft, "↙ Fram vänster", 1, 0), (ProbeCorner.FrontRight, "Fram höger ↘", 1, 1) })
        {
            var b = new Button { Content = text, Margin = new Thickness(2), Padding = new Thickness(6, 4, 6, 4), Foreground = Ink, Cursor = Cursors.Hand };
            var cc = c;
            b.Click += (_, _) => { if (!_running) { _corner = cc; Refresh(); } };
            Grid.SetRow(b, row); Grid.SetColumn(b, col); corners.Children.Add(b);
            _cornerButtons[c] = b;
        }
        _cornerRow.Children.Add(new TextBlock { Text = "Vilket hörn / vilken kant av materialet?", Foreground = Soft, Margin = new Thickness(0, 0, 0, 4) });
        _cornerRow.Children.Add(corners);

        var advanced = new Expander
        {
            Header = new TextBlock { Text = "AVANCERAT – hastigheter och avstånd", FontWeight = FontWeights.Bold, FontSize = 11.5, Foreground = B(0xA5, 0xC0, 0xD9) },
            Margin = new Thickness(0, 6, 0, 0),
            Content = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Children =
            {
                Pair("Snabb sökning (mm/min)", _fast, "Första sökningen.", "Långsam (mm/min)", _slow, "Andra, mätande beröringen."),
                Pair("Backa (mm)", _retract, "Mellan snabb och långsam beröring.", "Sök Z högst (mm)", _maxZ, "Ingen kontakt inom detta → stopp (ALARM:5)."),
                Pair("Sök X/Y högst (mm)", _maxXY, "Extra sökväg in mot plattans sida.", "Lyft efteråt (mm)", _lift, "Höjd över materialet när probningen är klar."),
            } }
        };

        _test.Click += async (_, _) => await TestContact();
        _start.Click += async (_, _) => await Run();
        _close.Click += (_, _) => { if (_running) Stop(); else Close(); };
        foreach (var b in new[] { _start, _test, _close })
        {
            b.Padding = new Thickness(16, 8, 16, 8); b.Margin = new Thickness(0, 0, 8, 0); b.Foreground = Brushes.White; b.BorderBrush = B(0x50, 0x67, 0x83);
            b.Background = b == _start ? B(0x2E, 0x7A, 0x4A) : B(0x34, 0x47, 0x5A);
        }
        Closing += (_, e) => { if (_running) { e.Cancel = true; } };

        var left = new StackPanel { Width = 320, Margin = new Thickness(0, 0, 16, 0), Children =
        {
            new Border { Background = Card, CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Child = _sketch },
            _how, _warn
        } };
        var right = new StackPanel { Children =
        {
            _cornerRow,
            new Border { Background = Card, CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Child = new StackPanel { Children = { _fields, advanced } } },
        } };
        var middle = new DockPanel();
        DockPanel.SetDock(left, Dock.Left); middle.Children.Add(left); middle.Children.Add(right);

        Content = new StackPanel { Margin = new Thickness(18), Children =
        {
            new TextBlock { Text = "PROBNING", FontSize = 20, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Välj vad som ska probas. Sätt klämman på fräsen och lägg plattan som skissen visar.", Foreground = Soft, Margin = new Thickness(0, 2, 0, 8) },
            tiles, middle,
            new WrapPanel { Margin = new Thickness(0, 14, 0, 10), Children = { _start, _test, _close } },
            new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), MinHeight = 56, Child = _log },
        } };
        Refresh();
        _log.Text = "Redo.";
    }

    private bool UsesXY => _kind != ProbeKind.Z;
    private bool UsesZ => _kind is ProbeKind.Z or ProbeKind.XYZ;
    private bool UsesX => _kind is ProbeKind.X or ProbeKind.XY or ProbeKind.XYZ;
    private bool UsesY => _kind is ProbeKind.Y or ProbeKind.XY or ProbeKind.XYZ;

    private void Refresh()
    {
        foreach (var (k, b) in _tiles) b.Background = k == _kind ? TileOn : TileOff;
        foreach (var (c, b) in _cornerButtons)
        {
            // X only cares about left/right, Y only about front/back: light up every corner that gives the same edge.
            bool on = _kind switch
            {
                ProbeKind.X => ProbeCycle.Side(c, 0) == ProbeCycle.Side(_corner, 0),
                ProbeKind.Y => ProbeCycle.Side(c, 1) == ProbeCycle.Side(_corner, 1),
                _ => c == _corner
            };
            b.Background = on ? TileOn : TileOff;
        }
        _cornerRow.Visibility = UsesXY ? Visibility.Visible : Visibility.Collapsed;

        _fields.Children.Clear();
        _fields.Children.Add(new TextBlock { Text = "INSTÄLLNINGAR FÖR " + KindName(_kind).ToUpperInvariant(), FontWeight = FontWeights.Bold, FontSize = 11.5,
            Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 0, 0, 6) });
        _fields.Children.Add(Pair("Plattans tjocklek (mm)", _thick, "Plattans ovansida ligger så här högt över materialet.",
            UsesXY ? "Sidodjup (mm)" : null, UsesXY ? _depth : null, "Så långt under plattans ovansida fräsen rör sidorna."));
        if (UsesXY) _fields.Children.Add(Pair("Fräsens diameter (mm)", _tool, "Kanten ligger fräsradien längre in än kontaktpunkten.", "Ut åt sidan (mm)", _out,
            "Så långt ut från startpunkten innan fräsen söker in mot plattans sida."));
        if (UsesX || UsesY) _fields.Children.Add(Pair(UsesX ? "Vägg X (mm)" : null, UsesX ? _wallX : null, "Från plattans yttersida till materialets kant i X.",
            UsesY ? "Vägg Y (mm)" : null, UsesY ? _wallY : null, "Från plattans yttersida till materialets kant i Y."));

        _how.Text = _kind switch
        {
            ProbeKind.Z => "Lägg plattan på materialet och ställ fräsen några mm ovanför plattan. Fräsen söker nedåt, rör plattan två gånger (snabbt, sedan långsamt) och sätter Z0 på materialytan.",
            ProbeKind.XYZ => $"Lägg hörnplattan i materialets {CornerText(_corner)} hörn. Ställ fräsen över plattan nära hörnet. Först Z på ovansidan, sedan går fräsen ut och ned bredvid plattan och rör båda sidorna. X0 Y0 hamnar i materialets hörn.",
            ProbeKind.XY => $"Z måste vara nollad först (Z-probning eller Nolla Z). Lägg hörnplattan i materialets {CornerText(_corner)} hörn och ställ fräsen över plattan nära hörnet. Fräsen lyfter, går ut, ned bredvid plattan och söker in mot båda sidorna. Z ändras inte.",
            ProbeKind.X => $"Z måste vara nollad först. Lägg plattan mot materialets {(ProbeCycle.Side(_corner, 0) > 0 ? "vänstra" : "högra")} kant och ställ fräsen över plattan. Fräsen går ut, ned bredvid och söker in mot sidan. Bara X sätts.",
            _ => $"Z måste vara nollad först. Lägg plattan mot materialets {(ProbeCycle.Side(_corner, 1) > 0 ? "främre" : "bakre")} kant och ställ fräsen över plattan. Fräsen går ut, ned bredvid och söker in mot sidan. Bara Y sätts.",
        };
        bool cornerIsZero = _corner == ProbeCorner.FrontLeft;
        _warn.Text = UsesXY && _projectZeroIsCentre ? "Obs: projektets nollpunkt X/Y är materialets MITT – probningen sätter nollan på kanten/hörnet."
            : (_kind is ProbeKind.XY or ProbeKind.XYZ) && !cornerIsZero ? "Obs: projektets nollpunkt är främre vänstra hörnet. Probar du ett annat hörn hamnar X0 Y0 där – ändra projektets nollpunkt eller probe:a fram vänster."
            : "";
        _warn.Visibility = _warn.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DrawSketch();
    }

    /// <summary>A top view (back at the top): the stock, the plate and the cutter, with the probing moves as arrows.</summary>
    private void DrawSketch()
    {
        _sketch.Children.Clear();
        double sx = 40, sy = 30, sw = 220, sh = 150;               // the stock
        Add(new Rectangle { Width = sw, Height = sh, Fill = Wood, Opacity = 0.85 }, sx, sy);
        Add(new TextBlock { Text = "material", Foreground = B(0x5A, 0x40, 0x1E), FontSize = 11 }, sx + sw / 2 - 22, sy + sh / 2 - 8);
        Add(new TextBlock { Text = "bak", Foreground = Muted, FontSize = 10 }, sx + sw / 2 - 8, 6);
        Add(new TextBlock { Text = "fram", Foreground = Muted, FontSize = 10 }, sx + sw / 2 - 10, sy + sh + 8);
        int dx = ProbeCycle.Side(_corner, 0), dy = ProbeCycle.Side(_corner, 1);  // +1: left / front
        double cornerX = dx > 0 ? sx : sx + sw, cornerY = dy > 0 ? sy + sh : sy;
        double plate = 70, wall = 12, r = 9;

        if (_kind == ProbeKind.Z)
        {
            Add(new Rectangle { Width = plate, Height = plate, Fill = Plate, Stroke = Ink, StrokeThickness = 1 }, sx + sw / 2 - plate / 2, sy + sh / 2 - plate / 2);
            Bit(sx + sw / 2, sy + sh / 2);
            ArrowLine(sx + sw / 2 + 24, sy + sh / 2 - 26, sx + sw / 2 + 24, sy + sh / 2 + 6);
            Add(new TextBlock { Text = "Z ↓", Foreground = Arrow, FontWeight = FontWeights.Bold }, sx + sw / 2 + 30, sy + sh / 2 - 22);
            return;
        }
        // The plate sits on the corner (or edge) and reaches over its outer sides by the wall.
        double px = dx > 0 ? cornerX - wall : cornerX - plate + wall;
        double py = dy > 0 ? cornerY - plate + wall : cornerY - wall;
        if (_kind == ProbeKind.X) { py = sy + sh / 2 - plate / 2; }
        if (_kind == ProbeKind.Y) { px = sx + sw / 2 - plate / 2; }
        Add(new Rectangle { Width = plate, Height = plate, Fill = Plate, Stroke = Ink, StrokeThickness = 1, Opacity = 0.95 }, px, py);
        double bx = px + plate / 2, by = py + plate / 2;
        Bit(bx, by);
        if (UsesX)
        {
            double outX = dx > 0 ? px - 26 : px + plate + 26;
            ArrowLine(bx, by, outX, by, dashed: true);
            ArrowLine(outX, by + 14, dx > 0 ? px - 2 : px + plate + 2, by + 14);
            Add(new TextBlock { Text = "X", Foreground = Arrow, FontWeight = FontWeights.Bold }, outX - 4, by + 18);
        }
        if (UsesY)
        {
            double outY = dy > 0 ? py + plate + 26 : py - 26;
            ArrowLine(bx, by, bx, outY, dashed: true);
            ArrowLine(bx - 14, outY, bx - 14, dy > 0 ? py + plate + 2 : py - 2);
            Add(new TextBlock { Text = "Y", Foreground = Arrow, FontWeight = FontWeights.Bold }, bx - 30, outY - 8);
        }
        if (_kind is ProbeKind.XY or ProbeKind.XYZ)
            Add(new Ellipse { Width = 10, Height = 10, Fill = B(0xFF, 0x4D, 0x4D) }, cornerX - 5, cornerY - 5);   // X0 Y0
        if (_kind == ProbeKind.XYZ) Add(new TextBlock { Text = "Z ↓ först", Foreground = Arrow, FontSize = 11 }, bx + 12, by - 22);

        void Bit(double x, double y) =>
            Add(new Ellipse { Width = 2 * r, Height = 2 * r, Fill = B(0x4F, 0xB3, 0xFF), Stroke = Ink, StrokeThickness = 1.2 }, x - r, y - r);
    }

    private void ArrowLine(double x1, double y1, double x2, double y2, bool dashed = false)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Arrow, StrokeThickness = 2 };
        if (dashed) { line.StrokeDashArray = new DoubleCollection { 3, 2 }; line.Opacity = 0.7; }
        _sketch.Children.Add(line);
        if (dashed) return;
        double a = Math.Atan2(y2 - y1, x2 - x1), h = 8;
        var head = new Polygon { Fill = Arrow, Points =
        {
            new Point(x2, y2),
            new Point(x2 - h * Math.Cos(a - 0.45), y2 - h * Math.Sin(a - 0.45)),
            new Point(x2 - h * Math.Cos(a + 0.45), y2 - h * Math.Sin(a + 0.45))
        } };
        _sketch.Children.Add(head);
    }

    private void Add(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); _sketch.Children.Add(e); }

    private static string KindName(ProbeKind k) => k switch { ProbeKind.Z => "Z", ProbeKind.X => "X", ProbeKind.Y => "Y", ProbeKind.XY => "XY", _ => "XYZ" };
    private static string CornerText(ProbeCorner c) => c switch
    {
        ProbeCorner.FrontLeft => "främre vänstra", ProbeCorner.FrontRight => "främre högra", ProbeCorner.BackLeft => "bakre vänstra", _ => "bakre högra"
    };

    /// <summary>Waits for the probe input (Pn:P) while the user touches the plate to the tool.</summary>
    private async Task TestContact()
    {
        _test.IsEnabled = false;
        Say("Rör plattan mot fräsen nu (inom 10 sekunder) …", Soft);
        try
        {
            for (int i = 0; i < 40; i++)
            {
                var st = await _grbl.RequestStatusAsync(TimeSpan.FromSeconds(1));
                if (st?.Pins?.Contains('P') == true) { Say("Kontakt! Proben fungerar. Lägg plattan på plats.", Good); return; }
                await Task.Delay(250);
            }
            Say("Ingen kontakt kändes. Kontrollera kabeln, klämman och att fräsen leder ström.", Bad);
        }
        catch (Exception ex) { Say("Kunde inte läsa proben: " + ex.Message, Bad); }
        finally { _test.IsEnabled = true; }
    }

    private async Task Run()
    {
        if (!ReadSettings(out var tool)) return;
        if (ProbeValidation.GetError(_s, _kind, tool) is string error) { Say(error, Bad); return; }
        _s.LastKind = _kind; _s.Corner = _corner;
        try { AppStorage.Current.SaveProbeSettings(_s); } catch (Exception) { /* the probe still runs with the values */ }
        SetRunning(true);
        _log.Foreground = Soft; _log.Text = "";
        var progress = new Progress<string>(line => _log.Text += (_log.Text.Length > 0 ? "\n" : "") + line);
        try
        {
            var result = await new ProbeCycle(_grbl, _s).RunAsync(_kind, tool, progress, default, _corner);
            _log.Text += (_log.Text.Length > 0 ? "\n\n" : "") + result.Message;
            _log.Foreground = result.Success ? Good : Bad;
            if (result.Success) _onDone(result);
        }
        catch (Exception ex) { _log.Foreground = Bad; _log.Text += "\n" + ex.Message; }
        finally { SetRunning(false); }
    }

    private void SetRunning(bool on)
    {
        _running = on;
        _start.IsEnabled = _test.IsEnabled = !on;
        _close.Content = on ? "■  STOPP" : "Stäng";
        _close.Background = on ? B(0x8A, 0x2E, 0x26) : B(0x34, 0x47, 0x5A);
    }

    /// <summary>Stops the probe at once (soft reset); the cycle then ends with a message.</summary>
    private void Stop()
    {
        try { _grbl.SendRealtime(GrblProtocol.SoftReset); } catch (Exception) { }
        Say("Stoppad. Maskinen kan behöva homing innan den används igen.", Warn);
    }

    private bool ReadSettings(out double tool)
    {
        tool = 0;
        if (UsesXY && (!P(_tool, out tool) || tool <= 0)) { Say("Ange fräsens diameter i mm.", Bad); return false; }
        var fields = new (TextBox Box, Action<double> Set)[]
        {
            (_thick, v => _s.PlateThicknessMm = v), (_wallX, v => _s.PlateWallXMm = v), (_wallY, v => _s.PlateWallYMm = v),
            (_fast, v => _s.FastFeed = v), (_slow, v => _s.SlowFeed = v), (_maxZ, v => _s.MaxTravelZMm = v), (_maxXY, v => _s.MaxTravelXYMm = v),
            (_retract, v => _s.RetractMm = v), (_out, v => _s.SideMoveOutMm = v), (_depth, v => _s.SideDepthMm = v), (_lift, v => _s.FinalLiftMm = v)
        };
        foreach (var (box, set) in fields)
        {
            if (!P(box, out var v)) { Say("Alla mått och hastigheter måste vara tal.", Bad); box.Focus(); return false; }
            set(v);
        }
        return true;
    }

    private void Say(string text, Brush b) { _log.Text = text; _log.Foreground = b; }

    private static Grid Pair(string? a, TextBox? ba, string? ta, string? b, TextBox? bb, string? tb)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition());
        void Cell(string? label, TextBox? box, string? tip, int col)
        {
            if (label is null || box is null) return;
            box.ToolTip = tip;
            if (box.Parent is Panel old) old.Children.Remove(box);    // the same field is reused when the type changes
            var s = new StackPanel { Margin = new Thickness(col == 0 ? 0 : 6, 0, col == 0 ? 6 : 0, 0), Children =
                { new TextBlock { Text = label, Foreground = Muted, FontSize = 11, ToolTip = tip }, box } };
            Grid.SetColumn(s, col); g.Children.Add(s);
        }
        Cell(a, ba, ta, 0); Cell(b, bb, tb, 1);
        return g;
    }

    private static bool P(TextBox t, out double v) => double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    private static TextBox Field() => new() { Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 2, 0, 0) };
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
