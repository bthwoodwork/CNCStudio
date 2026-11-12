using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Connection to the GRBL controller. Nothing is sent unless the user acts, except '?' status
/// requests while connected.
/// </summary>
public partial class MainWindow
{
    private GrblClient? _grbl;
    private GrblConsoleWindow? _console;
    /// <summary>Simulated GRBL 1.1 machine. Its "EEPROM" ($$, G54–G59) is saved in %APPDATA%\CNCStudio\demomaskin.json, so what you set stays between program starts; the frame follows the saved $130–$132.</summary>
    private const string DemoPort = MachineLibrary.DemoPort;
    private SimulatedGrbl? _demoMachine;
    private static SimulatedGrbl NewDemoMachine() =>
        new(stateFile: System.IO.Path.Combine(AppStorage.Current.LocalFolder, "demomaskin.json"));
    private readonly List<string> _grblLog = new();
    /// <summary>The connection's log on disk (CNC Studio\Loggar\<dator>), written every couple of seconds.</summary>
    private SessionLogFile? _sessionLog;
    /// <summary>The connected machine's $$ settings (read on connect and after homing); null = not known.</summary>
    private IReadOnlyDictionary<int, double>? _grblSettings;

    /// <summary>
    /// Limits for jogging: the connected machine's own travel, homing direction and pull-off ($$, read on
    /// connect and after homing) – not the project's profile, which may be for another machine.
    /// </summary>
    private MachineProfile JogLimits => _grblSettings is { } s ? GrblSettings.ApplyToProfile(_document.Machine, s) : _document.Machine;
    private System.Windows.Threading.DispatcherTimer? _sessionLogTimer;

    internal GrblClient? Grbl => _grbl is { IsConnected: true } ? _grbl : null;
    internal IReadOnlyList<string> GrblLog => _grblLog;
    internal event Action<string>? GrblLogAdded;

    private void InitGrblUi() { RefreshPorts(); SetMachineControlsEnabled(false); InitJobUi(); }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        if (PortPicker is null) return;
        var selected = PortPicker.SelectedItem as string;
        var ports = SerialGrblTransport.AvailablePorts().Append(DemoPort).ToArray();
        PortPicker.ItemsSource = ports;
        PortPicker.SelectedItem = ports.Contains(selected) ? selected : ports.FirstOrDefault();
        if (_grbl is null)
            ConnectionDetailText.Text = ports.Length == 0 ? "Ingen COM-port hittades." : "Välj port och klicka Anslut.";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_grbl is not null) { DisconnectGrbl("Frånkopplad."); return; }
        if (PortPicker.SelectedItem is not string port) { Error("Välj en COM-port först."); return; }
        ConnectButton.IsEnabled = false;
        SetConnectionBanner("ANSLUTER…", $"{port}, {GrblProtocol.BaudRate} baud", Color.FromRgb(70, 80, 40));
        bool demo = port == DemoPort;
        UseMachineForPort(port);
        IGrblTransport transport = demo ? (_demoMachine ??= NewDemoMachine()) : new SerialGrblTransport(port);
        var client = new GrblClient(transport);
        StartSessionLog(port);
        client.LineReceived += line =>
        {
            Log("← " + GrblSettings.Describe(line));
            // An alarm or a reset (welcome) means the position may be lost: hide the tool until homed again.
            if (line.Kind is GrblLineKind.Alarm or GrblLineKind.Welcome)
                Dispatcher.BeginInvoke(() => { _homedThisSession = false; if (_machineView) RenderMachine(refreshCam: false); });
            if (line.Kind is GrblLineKind.Message or GrblLineKind.Welcome)
                Dispatcher.BeginInvoke(() => ShowResetState(client));
        };
        client.Sent += text => Log("→ " + text);
        client.StatusChanged += s => Dispatcher.BeginInvoke(() => ShowStatus(s));
        try
        {
            var hello = await client.ConnectAsync();
            _connectedFirmware = hello;
            _grbl = client;
            client.StartStatusPolling(TimeSpan.FromMilliseconds(250));
            _connectedBanner = (demo ? "ANSLUTEN – DEMOMASKIN" : "ANSLUTEN", $"{port} · {hello}", demo ? Color.FromRgb(92, 60, 140) : Color.FromRgb(36, 98, 63));
            SetConnectionBanner(_connectedBanner.Title, _connectedBanner.Detail, _connectedBanner.Color);
            ResetGrblButton.IsEnabled = true;
            ConnectButton.Content = "Koppla från";
            Log($"Ansluten till {port}: {hello}");
            StatusText.Text = $"Ansluten till GRBL på {port}. Inga kommandon skickas utan att du väljer det.";
            SetMachineControlsEnabled(true);
            RefreshMachinePicker();
            UpdateJobButtons(); RefreshRecoveryBanner();
            await ReadActiveWcsAsync(client);
            await ReadMachineSettingsAsync(client, offerProfileUpdate: true);
        }
        catch (Exception ex)
        {
            client.Dispose();
            Log($"Kunde inte ansluta: {ex.Message}");
            StopSessionLog();
            SetConnectionBanner("EJ ANSLUTEN", "Anslutningen misslyckades.", Color.FromRgb(83, 60, 52));
            Error($"Kunde inte ansluta till {port}: {ex.Message}");
        }
        finally { ConnectButton.IsEnabled = true; }
    }

    internal void DisconnectGrbl(string reason)
    {
        var client = _grbl;
        _grbl = null;
        if (client is not null)
        {
            try { client.Disconnect(); } catch (Exception) { }
            client.Dispose();
        }
        ConnectButton.Content = "Anslut";
        ResetGrblButton.IsEnabled = false; ResetGrblButton.ClearValue(BackgroundProperty);
        _homedThisSession = false; _lastLiveState = null; _grblSettings = null;
        UpdateJobButtons();
        if (_machineView) RenderMachine(refreshCam: false);
        SetConnectionBanner("EJ ANSLUTEN", reason, Color.FromRgb(83, 60, 52));
        LiveStateText.Text = "–";
        _jogHeld = false;
        SetMachineControlsEnabled(false);
        RefreshMachinePicker();
        Log(reason);
        StopSessionLog();
    }

    /// <summary>
    /// Reads $$ (allowed in Idle and Alarm, refused only during a cycle or hold – verified, system.c) so
    /// the tool marker uses the machine's own travel and homing corner. If the project's machine profile
    /// does not match the machine (e.g. a project made for 750 × 750 on a 500 × 500 machine), offers to
    /// take the machine's values.
    /// </summary>
    private async Task ReadMachineSettingsAsync(GrblClient g, bool offerProfileUpdate)
    {
        IReadOnlyDictionary<int, double> settings;
        try { settings = await g.ReadSettingsAsync(); }
        catch (Exception ex) { Log("Kunde inte läsa $$: " + ex.Message); return; }
        if (_grbl != g || settings.Count == 0) return;
        _grblSettings = settings;
        UpdateToolMarker();
        if (!offerProfileUpdate) return;
        var geometry = new[] { 23, 27, 130, 131, 132 };
        var diffs = GrblSettings.CompareWithProfile(_document.Machine, settings).Where(d => geometry.Contains(d.Setting)).ToList();
        if (diffs.Count == 0) return;
        Log("Maskinprofilen skiljer sig från maskinen: " + string.Join(", ", diffs.Select(d => $"${d.Setting} {d.ProfileValue}→{d.MachineValue}")));
        var travelWrites = GrblSettings.TravelWritesFromProfile(_document.Machine, settings);
        var buttons = new List<(string, MessageBoxResult)> { ("Använd maskinens värden i profilen", MessageBoxResult.Yes) };
        if (travelWrites.Count > 0) buttons.Add(("Skriv profilens arbetsyta till maskinen", MessageBoxResult.No));
        buttons.Add(("Låt det vara", MessageBoxResult.Cancel));
        var choice = AppDialog.Choose(this,
            $"Maskinprofilen \"{_document.Machine.Name}\" stämmer inte med den anslutna maskinen:\n\n" +
            string.Join("\n", diffs.Select(d => $"  {d.What} (${d.Setting}): profilen {d.ProfileValue}, maskinen {d.MachineValue}")) +
            "\n\nMaskinvyn, fräsens position och CAM-kontrollen räknar med profilens värden.\n\n" +
            $"• Använd maskinens värden – profilen \"{_document.Machine.Name}\" får maskinens värden.\n" +
            (travelWrites.Count > 0
                ? "• Skriv profilens arbetsyta till maskinen – " + string.Join(", ", travelWrites.Select(w => $"${w.Number}={w.Value:0.###}")) +
                  " skrivs till GRBL (en kopia av $$ sparas först). Välj det om profilen är rätt."
                : ""),
            "Maskinprofil", buttons, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (choice == MessageBoxResult.No) { await WriteTravelToMachineAsync(g, travelWrites); return; }
        if (choice != MessageBoxResult.Yes) return;
        RememberChange();
        _document.Machine = GrblSettings.ApplyToProfile(_document.Machine, settings);
        SaveDefaultMachineProfile(_document.Machine);
        RefreshMachineSummary(); RefreshMotionFields(); ShowStockFields(); Render();
        StatusText.Text = $"Maskinprofilen uppdaterad från maskinen: {_document.Machine.WorkWidthMm:0.#} × {_document.Machine.WorkLengthMm:0.#} mm.";
        if (MachineSetupValidation.PlacementProblem(_document) is string placement)
            AppDialog.Show(this, placement + "\n\nInga körbanor beräknas förrän materialet och fastspänningarna ligger inom arbetsområdet.",
                "Materialplacering", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Writes $130–$132 from the profile to the controller (backup copy first, values read back) and
    /// refreshes the settings the jog limits and the machine view use.
    /// </summary>
    private async Task<bool> WriteTravelToMachineAsync(GrblClient g, IReadOnlyList<(int Number, double Value)> writes)
    {
        var state = g.LastStatus?.State;
        if (state is not ("Idle" or "Alarm"))
        {
            Error($"Inställningar kan bara skrivas när maskinen är Idle eller i larm (nu: {state ?? "okänt"}).");
            return false;
        }
        var result = await g.WriteSettingsAsync(writes);
        if (!result.Success) { Error("Arbetsytan kunde inte skrivas till maskinen: " + result.Message); return false; }
        var text = string.Join(", ", writes.Select(w => $"${w.Number}={w.Value:0.###}"));
        Log("Profilens arbetsyta skriven till maskinen: " + text);
        await ReadMachineSettingsAsync(g, offerProfileUpdate: false);
        StatusText.Text = $"Maskinen har nu profilens arbetsyta ({text}).";
        if (state == "Idle" && _document.Machine.HomeOnStartup)
            AppDialog.Show(this, $"Skrivet: {text}.\n\nKör homing ($H) igen så att maskinens gränser räknas från den nya arbetsytan.",
                "Arbetsyta skriven", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>What the controller said when connecting (welcome or $I), written into setting copies.</summary>
    private string? _connectedFirmware;

    /// <summary>A copy of the settings before every change from CNC Studio (GrblClient.WriteSettingsAsync).</summary>
    private void InitSettingsBackup()
    {
        GrblClient.BeforeSettingsWrite = (settings, changes) =>
        {
            // Connecting picks the machine, so the project's machine is the connected one.
            var machine = _document.Machine;
            GrblBackup.Save(GrblBackup.MachineFolder(AppStorage.Current.GrblBackupFolder, machine), settings, DateTime.Now, AppStorage.ComputerName,
                _connectedFirmware, "Före ändring: " + string.Join(", ", changes.Select(c => $"${c.Number}={GrblBackup.Value(c.Number, c.Value)}")), machine);
        };
    }

    /// <summary>Kalibrera steg/mm for <paramref name="machine"/> – only the connected machine can be calibrated.</summary>
    private void CalibrateMachine(MachineProfile machine, Window owner)
    {
        if (_grbl is null || machine.Id != _document.Machine.Id)
        {
            AppDialog.Show(owner, $"Kalibreringen görs på den anslutna maskinen. Anslut till {machine.Name} först (Maskinkontroll → Anslut) och kör homing.",
                "Kalibrera steg/mm", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (RequireIdle("Kalibrering", dialog: true) is not GrblClient g) return;
        new StepsCalibrationWindow(g, (dx, dy, dz) => GrblMachineCommands.CheckJog(g.LastStatus, JogLimits, dx, dy, dz), machine.Name) { Owner = owner }.ShowDialog();
    }

    /// <summary>The $$ copies of <paramref name="machine"/>; comparing and restoring when it is the connected machine.</summary>
    private void ShowBackups(MachineProfile machine, Window owner)
    {
        bool connected = _grbl is not null && machine.Id == _document.Machine.Id;
        new GrblBackupWindow(machine, connected ? Grbl : null, connected ? _connectedFirmware : null) { Owner = owner }.ShowDialog();
    }

    private void StartSessionLog(string port)
    {
        StopSessionLog();
        try
        {
            _sessionLog = new SessionLogFile(ErrorLog.Folder, DateTime.Now,
                $"{AppTitle} – maskinstyrning på {AppStorage.ComputerName}, port {port}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception) { _sessionLog = null; return; }       // no log folder: keep the in-memory log only
        _sessionLogTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _sessionLogTimer.Tick += (_, _) => _sessionLog?.Flush();
        _sessionLogTimer.Start();
    }

    /// <summary>Writes what is left and closes the file (on disconnect and when the program closes).</summary>
    private void StopSessionLog()
    {
        _sessionLogTimer?.Stop(); _sessionLogTimer = null;
        var log = _sessionLog; _sessionLog = null;
        log?.Dispose();
    }

    private (string Title, string Detail, Color Color) _connectedBanner;

    /// <summary>
    /// After "Reset to continue" GRBL answers nothing but a soft reset: say so clearly and point at the
    /// Återställ button. The welcome after the reset restores the normal banner.
    /// </summary>
    private void ShowResetState(GrblClient client)
    {
        if (_grbl != client) return;
        if (client.ResetRequired)
        {
            SetConnectionBanner("LARM – ÅTERSTÄLL KRÄVS", "GRBL har stannat (t.ex. en brytare) och svarar inte förrän efter återställning. Klicka Återställ (Ctrl-X), kör sedan homing.",
                Color.FromRgb(150, 46, 38));
            ResetGrblButton.Background = new SolidColorBrush(Color.FromRgb(176, 42, 34));
            LiveStateText.Text = "Larm – återställ";               // GRBL sends no status until the reset
            LiveStateText.Foreground = Brushes.OrangeRed;
            JogStatusText.Text = GrblClient.ResetRequiredMessage;
        }
        else
        {
            SetConnectionBanner(_connectedBanner.Title, _connectedBanner.Detail, _connectedBanner.Color);
            ResetGrblButton.ClearValue(BackgroundProperty);
        }
    }

    private void ResetGrbl_Click(object sender, RoutedEventArgs e)
    {
        if (_grbl is not { IsConnected: true } g) return;
        if (_runner is not null && AppDialog.Show(this, "Ett program körs. Återställning stoppar det direkt (positionen kan gå förlorad om maskinen rör sig).\n\nÅterställ ändå?",
                "Återställ", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Log("Användaren: återställ (Ctrl-X).");
        g.SendRealtime(GrblProtocol.SoftReset);
        JogStatusText.Text = "GRBL återställd. Maskinen är låst tills homing ($H) körts.";
    }

    private void SetConnectionBanner(string title, string detail, Color color)
    {
        ConnectionBanner.Background = new SolidColorBrush(color);
        ConnectionStateText.Text = title;
        ConnectionDetailText.Text = detail;
    }

    private double[]? _lastWco;

    /// <summary>Shows state, MPos and WPos. WPos = MPos − WCO (report.c); WCO is only sent periodically.</summary>
    private void ShowStatus(GrblStatus s)
    {
        if (_grbl is null) return;
        if (s.Wco is not null) _lastWco = s.Wco;
        // Home → Idle means a homing cycle finished (also when $H was sent from the console).
        if (_lastLiveState == "Home" && s.State == "Idle" && !_homedThisSession)
        { _homedThisSession = true; if (_machineView) RenderMachine(refreshCam: false); }
        _lastLiveState = s.State;
        LiveStateText.Text = s.SubState is null ? s.State : $"{s.State}:{s.SubState}";
        LiveStateText.Foreground = s.State switch
        {
            "Alarm" => Brushes.OrangeRed, "Idle" => Brushes.LightGreen,
            "Run" or "Jog" or "Home" => Brushes.Gold, _ => Brushes.White
        };
        double[]? mpos = s.MPos, wpos = s.WPos;
        if (mpos is null && wpos is not null && _lastWco is not null) mpos = Sub(wpos, _lastWco, add: true);
        if (wpos is null && mpos is not null && _lastWco is not null) wpos = Sub(mpos, _lastWco, add: false);
        // Keep the computed MPos on the status so the jog travel check works whatever $10 reports.
        s.MPos ??= mpos;
        ShowDro(mpos, wpos);
        if (wpos is not null && _runner is not null) UpdateJobPathPosition(wpos);
        UpdateToolMarker();
        UpdateJobStatus(s);
    }

    private string? _lastLiveState;

    private static double[] Sub(double[] a, double[] b, bool add) =>
        a.Select((v, i) => i < b.Length ? (add ? v + b[i] : v - b[i]) : v).ToArray();

    private static string Vec(double[]? v) => v is null ? "–" :
        string.Join("  ", v.Select(x => x.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(9)));

    private void Log(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        _sessionLog?.Append(line);                               // thread-safe; written in batches
        Dispatcher.BeginInvoke(() =>
        {
            _grblLog.Add(line);
            if (_grblLog.Count > 2000) _grblLog.RemoveRange(0, 500);
            GrblLogAdded?.Invoke(line);
        });
    }

    /// <summary>
    /// MPos Z of work Z0 = WCO Z = G54 Z + G92 Z + TLO (verified: report.c WCO, $# output).
    /// Refused if units are inches ($13=1) or a work coordinate system other than G54 is active.
    /// Only fills the field; the user saves.
    /// </summary>
    private async void ReadZeroFromMachine_Click(object sender, RoutedEventArgs e)
    {
        if (Grbl is not GrblClient g) { Error("Anslut till maskinen först (Maskinkontroll-panelen)."); return; }
        try
        {
            var settings = await g.ReadSettingsAsync();
            if (settings.TryGetValue(13, out var inches) && inches == 1)
            { Error("Maskinen rapporterar i tum ($13=1). CNC Studio räknar i mm – värdet läses inte."); return; }
            var gc = await g.SendLineAsync("$G");
            var modes = gc.Lines.FirstOrDefault(l => l.Raw.StartsWith("[GC:", StringComparison.Ordinal))?.Raw ?? "";
            if (!modes.Contains(" G54 ") && !modes.Contains(" G54]"))
            { Error($"Aktivt koordinatsystem är inte G54 ({modes}). Värdet läses inte."); return; }
            var p = await g.ReadParametersAsync();
            if (!p.TryGetValue("G54", out var g54) || g54.Length < 3)
            { Error("Maskinen svarade inte med G54 i $#."); return; }
            double g92 = p.TryGetValue("G92", out var o) && o.Length >= 3 ? o[2] : 0;
            double tlo = p.TryGetValue("TLO", out var t) && t.Length >= 1 ? t[0] : 0;
            double stockTop = g54[2] + g92 + tlo;
            string F3(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
            if (stockTop >= 0)
            { Error($"G54 Z + G92 Z + TLO = {F3(stockTop)}. Det ska vara negativt (under Z-brytaren) – är Z-nollan satt och maskinen homad?"); return; }
            if (AppDialog.Show(this,
                    $"MPos Z vid materialytan = {F3(stockTop)} mm\n(G54 Z {F3(g54[2])} + G92 Z {F3(g92)} + verktygslängd {F3(tlo)})\n\n" +
                    "Gäller bara om maskinen är homad med $H och Z-nollan är satt på materialets ovansida.\n\nAnvänd värdet?",
                    "Läs Z-nolla från maskinen", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            ZMaxBox.Text = F3(stockTop);
            StatusText.Text = "MPos Z vid materialytan inläst från maskinen. Klicka Spara Z-höjder för att använda det.";
        }
        catch (Exception ex) { Error("Kunde inte läsa från maskinen: " + ex.Message); }
    }

    private LimitSwitchTestWindow? _limitTest;

    private void OpenLimitTest_Click(object sender, RoutedEventArgs e)
    {
        if (Grbl is not GrblClient g) { Error("Anslut till maskinen först (Maskinkontroll-panelen)."); return; }
        if (_limitTest is { IsLoaded: true }) { _limitTest.Activate(); return; }
        var w = new LimitSwitchTestWindow(g) { Owner = this };
        w.Closed += (_, _) =>
        {
            if (w.HardLimitsLeftOff)
            {
                SetConnectionBanner("HÅRDA GRÄNSER AV", "$21=0 kunde inte återställas. Skriv $21=1 i konsolen innan körning.",
                    Color.FromRgb(150, 46, 38));
                Log("VARNING: hårda gränser ($21) är fortfarande avstängda.");
            }
            else if (w.AllPassed) Log("Brytartest: X, Y och Z godkända.");
        };
        _limitTest = w;
        w.Show();
    }

    /// <summary>
    /// The guided first test has its own connection, so the main connection is closed first:
    /// only one program may talk to the controller at a time.
    /// </summary>
    private void FirstTest_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is not null) { Error("Ett program körs. Stoppa det först."); return; }
        if (_grbl is not null)
        {
            if (AppDialog.Show(this, "Första testet använder en egen anslutning. Koppla från maskinstyrningen och öppna testet?",
                    "Första test", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            DisconnectGrbl("Frånkopplad – första testet har en egen anslutning.");
        }
        var w = new FirstTestWindow(
            () => SerialGrblTransport.AvailablePorts().Append(DemoPort).ToList(),
            port => port == DemoPort ? (_demoMachine ??= NewDemoMachine()) : new SerialGrblTransport(port),
            _document.Machine,
            settings =>
            {
                RememberChange();
                _document.Machine = GrblSettings.ApplyToProfile(_document.Machine, settings);
                SaveDefaultMachineProfile(_document.Machine);
                RefreshMachineSummary(); ShowStockFields(); Render();
                return _document.Machine;
            }) { Owner = this };
        w.Closed += (_, _) => StatusText.Text = "Första testet stängt. Logg: " + w.LogPath;
        w.Show();
    }

    private void OpenConsole_Click(object sender, RoutedEventArgs e)
    {
        if (_console is { IsLoaded: true }) { _console.Activate(); return; }
        _console = new GrblConsoleWindow(this) { Owner = this };
        _console.Show();
    }
}
