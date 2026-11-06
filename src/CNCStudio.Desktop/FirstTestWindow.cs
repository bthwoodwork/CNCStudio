using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Guided first test against a real machine, isolated from the rest of the program: its own
/// connection, one step at a time, nothing moves without a click, short distances and slow speeds,
/// a STOP button that is always visible, and everything logged to a file.
/// Steps: preparations → connect → read and back up $$ → limit switches → axis directions ($3, before
/// homing) → homing → jog after homing → air run → summary.
/// </summary>
public sealed class FirstTestWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Panel = B(0x1F, 0x2E, 0x3F), Good = B(0x4C, 0xE0, 0x7A), Warn = B(0xFF, 0xB0, 0x3B), Bad = B(0xFF, 0x5C, 0x6C),
        Accent = B(0x2E, 0x7D, 0xD7);

    private readonly Func<IReadOnlyList<string>> _ports;
    private readonly Func<string, IGrblTransport> _transportFor;
    private MachineProfile _profile;
    private readonly Func<IReadOnlyDictionary<int, double>, MachineProfile> _applyToProfile;
    private readonly string[] _titles =
    {
        "Förberedelser", "Anslut", "Inställningar ($$)", "Gränslägesbrytare", "Axelriktning",
        "Homing", "Jogg efter homing", "Luftkörning", "Klart"
    };
    private readonly bool[] _done;
    private readonly string?[] _results;
    private readonly StackPanel _stepList = new(), _content = new();
    private readonly TextBlock _live = new() { Foreground = Soft, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly TextBox _logBox = new() { IsReadOnly = true, Height = 110, FontFamily = new FontFamily("Consolas"), FontSize = 11,
        Background = B(0x10, 0x1B, 0x27), Foreground = Soft, BorderBrush = B(0x3C, 0x51, 0x67), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _next = new() { Content = "Nästa →", MinWidth = 110 }, _back = new() { Content = "← Föregående", MinWidth = 110 };
    private readonly StreamWriter _log;
    private int _step;
    private GrblClient? _g;
    private IGrblTransport? _transport;
    private IReadOnlyDictionary<int, double>? _settings;
    private JobRunner? _runner;

    public string LogPath { get; }

    public FirstTestWindow(Func<IReadOnlyList<string>> ports, Func<string, IGrblTransport> transportFor,
        MachineProfile profile, Func<IReadOnlyDictionary<int, double>, MachineProfile> applyToProfile)
    {
        _ports = ports; _transportFor = transportFor; _profile = profile; _applyToProfile = applyToProfile;
        _done = new bool[_titles.Length];
        _results = new string?[_titles.Length];
        Title = "Första test mot maskinen";
        Width = 1060; Height = 820; MinWidth = 900; MinHeight = 640;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CNCStudio", "logs");
        Directory.CreateDirectory(folder);
        LogPath = Path.Combine(folder, $"forsta-test-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        _log = new StreamWriter(LogPath, append: false) { AutoFlush = true };
        Log("Första test startat.");
        Content = BuildLayout();
        _next.Click += (_, _) => Go(_step + 1);
        _back.Click += (_, _) => Go(_step - 1);
        Closing += OnClosing;
        Go(0);
    }

    // ---- Layout ----

    private UIElement BuildLayout()
    {
        var stop = new Button { Content = "■  STOPP", Background = B(0xB0, 0x2A, 0x22), Foreground = Brushes.White, FontSize = 18,
            FontWeight = FontWeights.Bold, Padding = new Thickness(24, 10, 24, 10), Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Stoppar all rörelse: avbryter jogg, bromsar in (feed hold) och återställer sedan GRBL. Positionen behålls om maskinen hann stanna." };
        stop.Click += async (_, _) => await StopAll();
        var reset = new Button { Content = "Återställ (Ctrl-X)", Padding = new Thickness(12, 6, 12, 6),
            ToolTip = "Mjuk återställning – behövs efter ett larm från en brytare (\"Reset to continue\")." };
        reset.Click += (_, _) => { if (_g is { IsConnected: true } g) { Log("Användaren: återställ (Ctrl-X)."); g.SendRealtime(GrblProtocol.SoftReset); } };
        var top = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
        var stopPanel = new StackPanel { Orientation = Orientation.Horizontal };
        stopPanel.Children.Add(stop); stopPanel.Children.Add(reset);
        DockPanel.SetDock(stopPanel, Dock.Right); top.Children.Add(stopPanel);
        top.Children.Add(new StackPanel { Children = {
            new TextBlock { Text = "FÖRSTA TEST MOT MASKINEN", FontSize = 20, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Ett steg i taget. Inget rör sig utan att du klickar. Håll handen nära strömbrytaren.", Foreground = Soft },
            _live } });

        var left = new Border { Background = Panel, CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Width = 230,
            Margin = new Thickness(16, 0, 10, 0), Child = _stepList };
        var right = new Border { Background = Panel, CornerRadius = new CornerRadius(6), Padding = new Thickness(20, 16, 20, 16),
            Child = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        var middle = new DockPanel { Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(left, Dock.Left); middle.Children.Add(left); middle.Children.Add(right);

        var nav = new DockPanel { Margin = new Thickness(16, 8, 16, 6) };
        DockPanel.SetDock(_next, Dock.Right); nav.Children.Add(_next);
        DockPanel.SetDock(_back, Dock.Left); nav.Children.Add(_back);
        nav.Children.Add(new TextBlock { Text = "Logg sparas i " + LogPath, Foreground = Muted, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        var log = new Border { Margin = new Thickness(16, 0, 16, 14), Child = _logBox };

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(log, Dock.Bottom); root.Children.Add(log);
        DockPanel.SetDock(nav, Dock.Bottom); root.Children.Add(nav);
        root.Children.Add(middle);
        return root;
    }

    private void Go(int step)
    {
        if (step < 0 || step >= _titles.Length) return;
        if (_runner is not null) { Note("Vänta tills luftkörningen är klar eller stoppa den.", Warn); return; }
        _step = step;
        _stepList.Children.Clear();
        for (int i = 0; i < _titles.Length; i++)
        {
            string mark = _done[i] ? "✓" : i == step ? "▶" : "·";
            _stepList.Children.Add(new TextBlock
            {
                Text = $"{mark}  {i + 1}. {_titles[i]}", Margin = new Thickness(2, 4, 2, 4), FontSize = 13,
                FontWeight = i == step ? FontWeights.Bold : FontWeights.Normal,
                Foreground = _done[i] ? Good : i == step ? Ink : Muted
            });
        }
        _content.Children.Clear();
        _content.Children.Add(new TextBlock { Text = $"{step + 1}. {_titles[step]}", FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
        switch (step)
        {
            case 0: StepPrepare(); break;
            case 1: StepConnect(); break;
            case 2: StepSettings(); break;
            case 3: StepSwitches(); break;
            case 4: StepDirections(); break;
            case 5: StepHoming(); break;
            case 6: StepJog(); break;
            case 7: StepAirRun(); break;
            default: StepSummary(); break;
        }
        UpdateNav();
    }

    private void UpdateNav()
    {
        _back.IsEnabled = _step > 0 && _runner is null;
        _next.IsEnabled = _step < _titles.Length - 1 && _done[_step] && _runner is null;
        _next.Content = _step == _titles.Length - 2 ? "Sammanfattning →" : "Nästa →";
    }

    private void Complete(string result)
    {
        _done[_step] = true;
        _results[_step] = result;
        Log($"Steg {_step + 1} ({_titles[_step]}) klart: {result}");
        int s = _step; Go(s);                                             // refresh marks
    }

    // ---- Steps ----

    private void StepPrepare()
    {
        Para("Det här testet går igenom maskinen i rätt ordning. Varje rörelse är kort och långsam. STOPP uppe till höger stoppar allt.");
        var items = new[]
        {
            "Fräsen (routern) är AVSTÄNGD och ingen fräs sitter i spännhylsan.",
            "Inget material eller fastspänningar ligger under fräsen.",
            "Jag når maskinens strömbrytare och kan bryta strömmen direkt.",
            "Z står inte ända uppe mot sin brytare (vrid ned den en bit för hand med strömmen av om den gör det).",
            "Inget ligger i vägen längs X- och Y-axlarna.",
        };
        var boxes = items.Select(t => new CheckBox { Content = t, Foreground = Ink, Margin = new Thickness(0, 5, 0, 5), IsChecked = _done[0] }).ToList();
        foreach (var b in boxes)
        {
            b.Checked += (_, _) => { if (boxes.All(x => x.IsChecked == true) && !_done[0]) Complete("Förberedelser bekräftade."); };
            _content.Children.Add(b);
        }
    }

    private void StepConnect()
    {
        if (_g is { IsConnected: true })
        {
            Para($"Ansluten: {_g.Version ?? "GRBL svarar"}.", Good);
            if (_transport is SimulatedGrbl) Para("ÖVNINGSLÄGE: demomaskinen är ansluten, inte den riktiga maskinen.", Warn);
            return;
        }
        Para("Välj porten där X-Carvens kontroller sitter (115200 baud). Välj DEMO för att öva utan maskin.");
        var picker = new ComboBox { ItemsSource = _ports(), Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
        picker.SelectedIndex = 0;
        _content.Children.Add(picker);
        var connect = Btn("Anslut", Accent);
        connect.Click += async (_, _) =>
        {
            if (picker.SelectedItem is not string port) return;
            connect.IsEnabled = false;
            try
            {
                _transport = _transportFor(port);
                var g = new GrblClient(_transport);
                g.LineReceived += l => Log("← " + GrblSettings.Describe(l));
                g.Sent += t => Log("→ " + t);
                g.StatusChanged += s => Dispatcher.BeginInvoke(() => ShowLive(s));
                var hello = await g.ConnectAsync();
                g.StartStatusPolling(TimeSpan.FromMilliseconds(250));
                _g = g;
                bool v11 = (g.Version ?? hello).Contains("1.1");
                Complete($"Ansluten till {port}: {hello}");
                if (!v11) Note("Kontrollern svarade inte som GRBL 1.1. CNC Studio är bara kontrollerat mot GRBL 1.1 – fortsätt inte om det inte är 1.1.", Bad);
            }
            catch (Exception ex) { Note("Kunde inte ansluta: " + ex.Message, Bad); connect.IsEnabled = true; }
        };
        _content.Children.Add(connect);
    }

    private void StepSettings()
    {
        Para("CNC Studio läser maskinens hela inställningsminne ($$), sparar en säkerhetskopia och kontrollerar det viktigaste. Inget skrivs utan att du godkänner.");
        var read = Btn("Läs inställningar ($$)", Accent);
        read.Click += async (_, _) => { await ReadSettings(); };
        _content.Children.Add(read);
        if (_settings is not null) ShowSettingsReview();
    }

    private async Task ReadSettings()
    {
        if (!Connected()) return;
        try
        {
            _settings = await _g!.ReadSettingsAsync();
            var backup = Path.Combine(Path.GetDirectoryName(LogPath)!, $"$$-backup-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllLines(backup, _settings.Select(kv => $"${kv.Key}={kv.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));
            Log("Säkerhetskopia av $$: " + backup);
            Go(_step);
        }
        catch (Exception ex) { Note("Kunde inte läsa: " + ex.Message, Bad); }
    }

    private void ShowSettingsReview()
    {
        var checks = Commissioning.ReviewSettings(_settings!, _profile);
        foreach (var c in checks)
            Para((c.Level switch { CheckLevel.Ok => "✓ ", CheckLevel.Info => "• ", CheckLevel.Warning => "! ", _ => "✕ " }) + c.Text,
                c.Level switch { CheckLevel.Ok => Good, CheckLevel.Info => Soft, CheckLevel.Warning => Warn, _ => Bad });
        var fixes = new List<(int, double)>();
        if (_settings!.GetValueOrDefault(13) == 1) fixes.Add((13, 0));
        if (_settings.GetValueOrDefault(32) == 1) fixes.Add((32, 0));
        if (_settings.GetValueOrDefault(22) != 1) fixes.Add((22, 1));
        if (((int)_settings.GetValueOrDefault(23) & 4) != 0) fixes.Add((23, (int)_settings.GetValueOrDefault(23) & 3));
        if (fixes.Count > 0)
        {
            var fix = Btn("Rätta " + string.Join(", ", fixes.Select(f => $"${f.Item1}={f.Item2}")) + "…", Warn);
            fix.Click += async (_, _) =>
            {
                if (Ask($"Skriva {string.Join(", ", fixes.Select(f => $"${f.Item1}={f.Item2}"))} till maskinens minne? Värdena kontrolläses efteråt.") != true) return;
                var r = await _g!.WriteSettingsAsync(fixes);
                Log(r.Message);
                if (!r.Success) Note(r.Message, Bad); else await ReadSettings();
            };
            _content.Children.Add(fix);
        }
        var apply = Btn("Använd maskinens värden i projektets maskinprofil", null);
        apply.Click += (_, _) => { _profile = _applyToProfile(_settings!); Log("Maskinens värden användes i projektets profil."); Note("Projektets maskinprofil är uppdaterad med maskinens värden.", Good); };
        _content.Children.Add(apply);
        Para("Säkerhetskopian ligger i samma mapp som loggen.", Muted);
        if (checks.All(c => c.Level != CheckLevel.Stop)) { if (!_done[2]) Complete("Inställningar lästa och kontrollerade; säkerhetskopia sparad."); }
        else Note("Rätta punkterna markerade ✕ innan du går vidare.", Bad);
    }

    private void StepSwitches()
    {
        Para("Tryck in varje brytare för hand (X, Y, Z) och släpp. Maskinen rör sig inte. Med hårda gränser på ($21=1) erbjuder testet att stänga av dem tillfälligt – de slås alltid på igen och kontrolläses.");
        var open = Btn("Öppna brytartestet", Accent);
        open.Click += (_, _) =>
        {
            if (!Connected()) return;
            var w = new LimitSwitchTestWindow(_g!) { Owner = this };
            w.Closed += (_, _) =>
            {
                if (w.HardLimitsLeftOff) Note("Hårda gränser ($21) är fortfarande AV. Skriv $21=1 innan maskinen körs.", Bad);
                if (w.AllPassed) Complete("X-, Y- och Z-brytarna fungerar.");
                else Note("Alla tre brytarna blev inte godkända.", Warn);
            };
            w.Show();
        };
        _content.Children.Add(open);
        if (_transport is SimulatedGrbl sim)
        {
            Para("Övningsläge – tryck demomaskinens brytare här:", Warn);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < 3; i++)
            {
                int axis = i;
                var t = new System.Windows.Controls.Primitives.ToggleButton { Content = $"Brytare {"XYZ"[i]}", Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(12, 6, 12, 6) };
                t.Checked += (_, _) => sim.PressSwitch(axis, true);
                t.Unchecked += (_, _) => sim.PressSwitch(axis, false);
                row.Children.Add(t);
            }
            _content.Children.Add(row);
        }
        SkipButton("Hoppa över brytartestet");
    }

    private void StepDirections()
    {
        Para("Nu kontrolleras att varje axel går åt rätt håll ($3) – före homing, eftersom homingen bygger på det. Rätt: X+ åt HÖGER, Y+ BORT från dig, Z+ UPPÅT.");
        Para("Maskinen är låst efter start. Första rörelsen låser upp den med $X (efter fråga). Positionen är då okänd, så bara korta rörelser.", Muted);
        var dist = new TextBox { Text = "5", Width = 60, Margin = new Thickness(6, 0, 0, 0) };
        _content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8),
            Children = { new TextBlock { Text = "Sträcka (mm, högst 20):", VerticalAlignment = VerticalAlignment.Center }, dist } });
        var ok = new bool[3];
        for (int i = 0; i < 3; i++)
        {
            int axis = i;
            string name = "XYZ"[i].ToString();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock { Text = name, Width = 26, FontSize = 18, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            foreach (var sign in new[] { -1, 1 })
            {
                var b = Btn($"{name}{(sign > 0 ? "+" : "−")}", null);
                b.Click += async (_, _) => await TestMove(axis, sign, dist.Text);
                row.Children.Add(b);
            }
            var right = Btn("Rätt håll", Good); var wrong = Btn("Fel håll", Bad);
            right.Click += (_, _) =>
            {
                ok[axis] = true; Log($"{name}: rätt håll.");
                right.Content = "✓ Rätt håll";
                if (ok.All(x => x)) Complete("X, Y och Z går åt rätt håll.");
            };
            wrong.Click += async (_, _) => await FlipDirection(axis);
            row.Children.Add(new Border { Width = 20 });
            row.Children.Add(right); row.Children.Add(wrong);
            _content.Children.Add(row);
        }
        Para("Använd MINUS-knapparna: X− ska gå åt VÄNSTER, Y− MOT dig, Z− NEDÅT. Före homing räknar GRBL med att maskinen står på 0 (högsta punkten i området), så med mjuka gränser på ($20=1) stoppas alla plusrörelser med error:15 – det är normalt.", Warn);
    }

    private async Task TestMove(int axis, int sign, string distText)
    {
        if (!Connected()) return;
        if (!double.TryParse(distText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) || mm <= 0 || mm > 20)
        { Note("Ange en sträcka mellan 0 och 20 mm.", Warn); return; }
        var s = await _g!.RequestStatusAsync(TimeSpan.FromSeconds(2));
        if (s?.State == "Alarm")
        {
            if (Ask("Maskinen är låst (inte homad). Låsa upp med $X för att testa riktningen? Positionen är okänd – bara korta rörelser.") != true) return;
            var u = await _g.SendLineAsync("$X");
            if (!u.IsOk) { Note("$X: " + u.Describe(), Bad); return; }
        }
        else if (!GrblMachineCommands.StateAllowsJog(s?.State)) { Note($"Maskinen är i läge {s?.State ?? "okänt"} – vänta på Idle.", Warn); return; }
        double d = sign * mm;
        var line = GrblMachineCommands.Jog(axis == 0 ? d : 0, axis == 1 ? d : 0, axis == 2 ? d : 0, axis == 2 ? 200 : 500);
        try
        {
            var r = await _g.SendLineAsync(line);
            string expect = (axis, sign) switch { (0, 1) => "åt HÖGER", (0, _) => "åt VÄNSTER", (1, 1) => "BORT från dig", (1, _) => "MOT dig", (2, 1) => "UPPÅT", _ => "NEDÅT" };
            if (r.IsOk) Note($"{line} skickat – axeln ska gå {mm:0.#} mm {expect}.", Soft);
            else if (r.Completion.Code == 15) Note("GRBL stoppade rörelsen: utanför arbetsområdet (error:15). Före homing räknas det från där maskinen startade – testa åt andra hållet.", Warn);
            else Note($"{line}: {r.Describe()}", Bad);
        }
        catch (Exception ex) { Note(ex.Message, Bad); }
    }

    private async Task FlipDirection(int axis)
    {
        if (!Connected()) return;
        var settings = await _g!.ReadSettingsAsync();
        int dir = (int)settings.GetValueOrDefault(3), flipped = Commissioning.FlipDirection(dir, axis);
        string name = "XYZ"[axis].ToString();
        if (Ask($"{name} går åt fel håll. Vända {name}: skriva $3={flipped} (nu $3={dir}) till maskinens minne? Testa sedan {name} igen.") != true) return;
        var r = await _g.WriteSettingsAsync(new[] { (3, (double)flipped) });
        Log(r.Message);
        Note(r.Success ? $"$3={flipped} skrivet och kontrolläst. Testa {name} igen." : r.Message, r.Success ? Good : Bad);
    }

    private void StepHoming()
    {
        string corner = _settings is null ? "" : Commissioning.CornerText((int)_settings.GetValueOrDefault(23));
        Para($"Homing ($H): först går Z UPP mot sin brytare, sedan X och Y samtidigt mot brytarna {corner}. Håll handen vid strömbrytaren.");
        var home = Btn("Kör homing ($H)", Good);
        home.Click += async (_, _) =>
        {
            if (!Connected()) return;
            if (Ask("Kör homing nu? Maskinen rör sig tills den hittar brytarna.") != true) return;
            home.IsEnabled = false;
            try
            {
                var r = await _g!.SendLineAsync("$H", timeout: TimeSpan.FromMinutes(2));
                if (!r.IsOk) { Note("Homing avvisades: " + r.Describe(), Bad); home.IsEnabled = true; return; }
                _settings ??= await _g.ReadSettingsAsync();
                var s = await _g.RequestStatusAsync(TimeSpan.FromSeconds(2));
                var expected = Commissioning.ExpectedHomePosition(_settings);
                string V(double[]? v) => v is null ? "–" : string.Join("  ", v.Take(3).Select(x => x.ToString("0.000", CultureInfo.InvariantCulture)));
                bool match = s?.MPos is { Length: >= 3 } m && Enumerable.Range(0, 3).All(i => Math.Abs(m[i] - expected[i]) < 0.05);
                Note($"Homing klar. MPos {V(s?.MPos)} – väntat {V(expected)}.", match ? Good : Warn);
                var yes = Btn($"Ja – fräsen står {corner} och högst upp", Good);
                var no = Btn("Nej", Bad);
                yes.Click += (_, _) => Complete($"Homing klar, MPos {V(s?.MPos)}.");
                no.Click += (_, _) => Note("Då stämmer brytarhörnet ($23) eller en riktning ($3) inte. Gå tillbaka till Axelriktning, eller ändra hörnet i Maskininställningar och skriv $23 till maskinen.", Bad);
                _content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { yes, no } });
            }
            catch (Exception ex)
            {
                home.IsEnabled = true;
                Note("Homing stoppades: " + ex.Message, Bad);
                if (ex.Message.Contains("ALARM:9")) Para("ALARM:9 = brytaren hittades inte inom 1,5 × axelns längd. Troligen går axeln åt fel håll ($3) eller brytarhörnet ($23) är fel.", Warn);
                if (ex.Message.Contains("ALARM:8")) Para("ALARM:8 = brytaren släppte inte efter pull-off. Öka $27 något.", Warn);
                if (ex.Message.Contains("ALARM:1")) Para("ALARM:1 = en brytare löste ut. Klicka Återställ (Ctrl-X) och kör homing igen.", Warn);
            }
        };
        _content.Children.Add(home);
    }

    private void StepJog()
    {
        Para("Efter homing är positionen känd. Jogga 10 mm åt varje håll och se att det stämmer. CNC Studio stoppar joggar som går utanför arbetsområdet.");
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (label, dx, dy, dz) in new[] { ("X−", -10.0, 0.0, 0.0), ("X+", 10, 0, 0), ("Y−", 0, -10, 0), ("Y+", 0, 10, 0), ("Z−", 0, 0, -5), ("Z+", 0, 0, 5) })
        {
            var b = Btn(label, null);
            b.Click += async (_, _) =>
            {
                if (!Connected()) return;
                var s = await _g!.RequestStatusAsync(TimeSpan.FromSeconds(2));
                if (GrblMachineCommands.CheckJog(s, _profile, dx, dy, dz) is string problem) { Note(problem, Warn); return; }
                var r = await _g.SendLineAsync(GrblMachineCommands.Jog(dx, dy, dz, dz != 0 ? 200 : 1000));
                if (!r.IsOk) Note(r.Describe(), Warn);
            };
            grid.Children.Add(b);
        }
        _content.Children.Add(grid);
        var done = Btn("Allt går åt rätt håll", Good);
        done.Click += (_, _) => Complete("Jogg efter homing stämmer.");
        _content.Children.Add(done);
        SkipButton("Hoppa över");
    }

    private void StepAirRun()
    {
        Para("Luftkörning: maskinen ritar en kvadrat på 50 × 50 mm i luften (ingen fräs) från där den står och inåt från brytarna. " +
             "Z går först upp nära brytaren. Det här testar sändningen av ett program.");
        Para("Prova under körningen: Paus och Fortsätt, matning −10/+10 %, och gärna STOPP (då kan du köra igen).", Muted);
        var start = Btn("Starta luftkörning", Good);
        var pause = Btn("Paus / Fortsätt", null);
        var minus = Btn("Matning −10 %", null);
        var plus = Btn("Matning +10 %", null);
        pause.Click += (_, _) => { if (_g is { } g) g.SendRealtime(g.LastStatus?.State == "Hold" ? GrblProtocol.CycleStartResume : GrblProtocol.FeedHold); };
        minus.Click += (_, _) => _g?.SendRealtime(GrblProtocol.FeedOverrideCoarseMinus);
        plus.Click += (_, _) => _g?.SendRealtime(GrblProtocol.FeedOverrideCoarsePlus);
        start.Click += async (_, _) =>
        {
            if (!Connected()) return;
            if (_g!.LastStatus?.State != "Idle") { Note("Maskinen måste vara Idle (homad).", Warn); return; }
            _settings ??= await _g.ReadSettingsAsync();
            var lines = Commissioning.AirSquare(_settings, 50, 800, _profile.LimitMarginMm);
            if (Ask("Starta luftkörningen?\n\n" + string.Join("\n", lines)) != true) return;
            _runner = new JobRunner(_g, lines, null);
            UpdateNav(); start.IsEnabled = false;
            var result = await _runner.RunAsync();
            _runner = null; start.IsEnabled = true; UpdateNav();
            _g.SendRealtime(GrblProtocol.FeedOverrideReset);
            if (result.Outcome == JobOutcome.Finished) Complete("Luftkörningen gick klart.");
            else Note(result.Message + " Du kan starta igen.", result.Outcome == JobOutcome.Stopped ? Warn : Bad);
        };
        _content.Children.Add(new WrapPanel { Children = { start, pause, minus, plus } });
        SkipButton("Hoppa över");
    }

    private void StepSummary()
    {
        for (int i = 0; i < _titles.Length - 1; i++)
            Para($"{(_done[i] ? "✓" : "–")}  {i + 1}. {_titles[i]}: {_results[i] ?? "inte gjort"}", _done[i] ? Good : Warn);
        Para(_transport is SimulatedGrbl
            ? "Det här var en övning mot demomaskinen. Gör samma sak mot den riktiga maskinen."
            : "Nästa steg: spänn fast material, nolla X, Y och Z på materialet och kör ett enkelt program med låg matning. Stå kvar vid maskinen.", Soft);
        Para("Logg: " + LogPath, Muted);
        var open = Btn("Öppna loggmappen", null);
        open.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(LogPath)!);
        _content.Children.Add(open);
    }

    // ---- Safety ----

    /// <summary>Jog cancel and feed hold at once; then, when stopped, a soft reset (position kept).</summary>
    private async Task StopAll()
    {
        Log("Användaren: STOPP.");
        if (_runner is JobRunner r) { await r.StopAsync(); return; }
        if (_g is not { IsConnected: true } g) return;
        g.SendRealtime(GrblProtocol.JogCancel);
        g.SendRealtime(GrblProtocol.FeedHold);
        for (int i = 0; i < 40; i++)
        {
            var s = await g.RequestStatusAsync(TimeSpan.FromSeconds(1));
            if (s is null || s.State is "Idle" or "Alarm") return;
            if (s.State == "Hold" && s.SubState == "0") { g.SendRealtime(GrblProtocol.SoftReset); return; }
            await Task.Delay(100);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_runner is not null) { e.Cancel = true; Note("Stoppa luftkörningen först.", Warn); return; }
        Log("Testet stängt.");
        if (_g is not null) { try { _g.Disconnect(); } catch (Exception) { } _g.Dispose(); }
        _log.Dispose();
    }

    // ---- Helpers ----

    private bool Connected()
    {
        if (_g is { IsConnected: true }) return true;
        Note("Inte ansluten – gå tillbaka till steg 2.", Bad);
        return false;
    }

    private void ShowLive(GrblStatus s)
    {
        string V(double[]? v) => v is null ? "–" : string.Join("  ", v.Take(3).Select(x => x.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(9)));
        _live.Text = $"Läge: {s.State}{(s.SubState is null ? "" : ":" + s.SubState)}   MPos {V(s.MPos)}   Brytare: {(string.IsNullOrEmpty(s.Pins) ? "–" : s.Pins)}";
        _live.Foreground = s.State == "Alarm" ? Bad : s.State == "Idle" ? Good : Warn;
    }

    private void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        try { _log?.WriteLine(line); } catch (ObjectDisposedException) { }
        Dispatcher.BeginInvoke(() =>
        {
            _logBox.AppendText(line + Environment.NewLine);
            if (_logBox.LineCount > 400) _logBox.Text = string.Join(Environment.NewLine, _logBox.Text.Split(Environment.NewLine).TakeLast(300));
            _logBox.ScrollToEnd();
        });
    }

    private void Para(string text, Brush? color = null) =>
        _content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = color ?? Ink, FontSize = 13.5, Margin = new Thickness(0, 3, 0, 6) });

    private void Note(string text, Brush color) { Para(text, color); Log(text); }

    private static Button Btn(string text, Brush? color)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 6, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Left, Foreground = Brushes.White,
            Background = color is SolidColorBrush c ? new SolidColorBrush(Color.FromRgb((byte)(c.Color.R * 0.55), (byte)(c.Color.G * 0.55), (byte)(c.Color.B * 0.55))) : B(0x34, 0x47, 0x5A),
            BorderBrush = color ?? B(0x50, 0x67, 0x83) };
        return b;
    }

    private void SkipButton(string text)
    {
        var b = Btn(text, null);
        b.Click += (_, _) => { if (Ask("Hoppa över det här steget? Det rekommenderas inte första gången.") == true) Complete("Överhoppat."); };
        _content.Children.Add(b);
    }

    private bool? Ask(string text) =>
        AppDialog.Show(this, text, "Första test", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
