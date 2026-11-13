using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Manual limit-switch test. Sends no motion. If hard limits are on, they can be switched off for
/// the test only with the user's confirmation, and are switched back on (and read back) when the
/// window closes.
/// </summary>
public partial class LimitSwitchTestWindow : Window
{
    private readonly GrblClient _grbl;
    private readonly LimitSwitchTest _test = new();
    private bool _restoreHardLimits;
    private bool _closingAfterRestore;

    /// <summary>True if the window closed while hard limits were still switched off.</summary>
    public bool HardLimitsLeftOff => _restoreHardLimits;
    public bool AllPassed => _test.AllPassed;
    private static readonly Brush Waiting = new SolidColorBrush(Color.FromRgb(52, 71, 90));
    private static readonly Brush Pressed = new SolidColorBrush(Color.FromRgb(196, 150, 20));
    private static readonly Brush Passed = new SolidColorBrush(Color.FromRgb(36, 120, 63));
    private static readonly Brush Stuck = new SolidColorBrush(Color.FromRgb(150, 46, 38));

    public LimitSwitchTestWindow(GrblClient grbl)
    {
        InitializeComponent();
        _grbl = grbl;
        _grbl.StatusChanged += OnStatus;
        _grbl.LineReceived += OnLine;
        Closed += (_, _) => { _grbl.StatusChanged -= OnStatus; _grbl.LineReceived -= OnLine; };
        Closing += OnClosing;
        Loaded += async (_, _) => await ReadSettingsAsync();
        Render();
    }

    /// <summary>True while hard limits are on and not yet switched off for the test: pressing would alarm.</summary>
    private bool HardLimitsBlockTest => HardLimitBox.Visibility == Visibility.Visible && !_restoreHardLimits;

    private async Task ReadSettingsAsync()
    {
        if (_grbl.ResetRequired)
        {
            SettingsText.Text = "GRBL väntar på återställning – klicka Återställ (Ctrl-X) nedan.";
            return;
        }
        try
        {
            var s = await _grbl.ReadSettingsAsync();
            int Get(int n) => s.TryGetValue(n, out var v) ? (int)v : -1;
            SettingsText.Text = $"Maskinens inställningar: $5={Get(5)} (inverterade brytaringångar), " +
                $"$21={Get(21)} (hårda gränser), $22={Get(22)} (homing).";
            HardLimitBox.Visibility = Get(21) == 1 && !_restoreHardLimits ? Visibility.Visible : Visibility.Collapsed;
            DisableHardLimitsButton.IsEnabled = true;
            Render();
        }
        catch (Exception ex) { SettingsText.Text = "Kunde inte läsa $$: " + ex.Message; }
    }

    private void OnStatus(GrblStatus s) => Dispatcher.BeginInvoke(() =>
    {
        _test.Update(s);
        LiveText.Text = $"Läge {s.State}  ·  Pn: {(s.Pins ?? "(ingen utlöst)")}";
        Render();
    });

    /// <summary>GRBL sends no status while it waits for a reset, so the alarm is shown from its message.</summary>
    private void OnLine(GrblLine line)
    {
        if (line.Kind is not (GrblLineKind.Alarm or GrblLineKind.Message)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_grbl.ResetRequired) return;
            ResultText.Foreground = Brushes.OrangeRed;
            ResultText.Text = "En brytare löste ut medan hårda gränser var på: GRBL har stannat (ALARM:1) och svarar inte förrän efter återställning. " +
                "Klicka Återställ (Ctrl-X), stäng sedan av hårda gränser för testet (steg 1) och tryck brytarna igen.";
        });
    }

    private void Render()
    {
        Show('X', LedX, TextX); Show('Y', LedY, TextY); Show('Z', LedZ, TextZ);
        if (_grbl.ResetRequired)
        {
            ResultText.Foreground = Brushes.OrangeRed;
            ResultText.Text = "GRBL har stannat efter ett larm och svarar inte förrän efter återställning. Klicka Återställ (Ctrl-X).";
        }
        else if (HardLimitsBlockTest)
        {
            ResultText.Foreground = B(0xFF, 0xD3, 0x9F);
            ResultText.Text = "Stäng först av hårda gränser för testet (steg 1 ovan). Annars stoppar GRBL vid första tryckningen.";
        }
        else if (_test.ActiveAtStart.Count > 0)
        {
            ResultText.Foreground = Brushes.OrangeRed;
            ResultText.Text = $"{string.Join(", ", _test.ActiveAtStart)} visar utlöst utan att du rör brytaren. " +
                "Kontrollera kabeln och om brytartypen stämmer med $5 (normalt öppen/sluten). Kör inte homing.";
        }
        else if (_test.AllPassed)
        {
            ResultText.Foreground = Brushes.LightGreen;
            ResultText.Text = "Alla tre brytarna fungerar. Nu kan homing ($H) köras.";
        }
        else
        {
            ResultText.Foreground = Brushes.White;
            ResultText.Text = "Tryck in och släpp brytarna en i taget.";
        }
    }

    private void Show(char axis, Border led, TextBlock text)
    {
        bool stuck = _test.ActiveAtStart.Contains(axis);
        bool on = _test.CurrentlyTriggered.Contains(axis);
        var state = _test.States[axis];
        bool blocked = HardLimitsBlockTest || _grbl.ResetRequired;
        led.Opacity = blocked ? 0.45 : 1;
        led.Background = stuck ? Stuck : state == SwitchTestState.Passed ? Passed : on ? Pressed : Waiting;
        text.Text = blocked ? "vänta – se nedan" : stuck ? "utlöst från start!" :
            state == SwitchTestState.Passed ? (on ? "OK ✓ (intryckt)" : "OK ✓") :
            state == SwitchTestState.Pressed ? "intryckt – släpp" : "tryck in brytaren";
    }

    private async void DisableHardLimits_Click(object sender, RoutedEventArgs e)
    {
        if (AppDialog.Show(this, "Skriva $21=0 (hårda gränser av) till maskinens minne under testet?\n\n" +
                "CNC Studio skriver tillbaka $21=1 och kontrolläser när du klickar Klar eller stänger fönstret. " +
                "Maskinen ska inte köras medan hårda gränser är av.", "Hårda gränser", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        DisableHardLimitsButton.IsEnabled = false;
        SettingWriteResult r;
        try { r = await _grbl.WriteSettingsAsync(new[] { (21, 0.0) }); }
        catch (Exception ex) { r = new SettingWriteResult(false, ex.Message); }
        if (r.Success)
        {
            _restoreHardLimits = true;
            DisableHardLimitsButton.Content = "Hårda gränser är tillfälligt av – återställs när du klickar Klar";
            _test.Reset(); ResultText.Text = ""; Render();
        }
        else
        {
            DisableHardLimitsButton.IsEnabled = true;
            AppDialog.Show(this, r.Message, "Kunde inte ändra $21", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Soft reset after a switch alarm; then the settings are read again and the test starts over.</summary>
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResetButton.IsEnabled = false;
            _grbl.SendRealtime(GrblProtocol.SoftReset);
            await Task.Delay(800);
            _test.Reset();
            ResultText.Text = "";
            await ReadSettingsAsync();
            Render();
        }
        catch (Exception ex) { ResultText.Foreground = Brushes.OrangeRed; ResultText.Text = "Återställningen misslyckades: " + ex.Message; }
        finally { ResetButton.IsEnabled = true; }
    }

    private static SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private void Restart_Click(object sender, RoutedEventArgs e) { _test.Reset(); ResultText.Text = ""; Render(); }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Hard limits are always restored (and read back) before the window may close.</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_restoreHardLimits || _closingAfterRestore) return;
        e.Cancel = true;
        DoneButton.IsEnabled = false;
        ResultText.Foreground = Brushes.White;
        ResultText.Text = "Slår på hårda gränser igen ($21=1) …";
        SettingWriteResult r;
        try { r = await _grbl.WriteSettingsAsync(new[] { (21, 1.0) }); }
        catch (Exception ex) { r = new SettingWriteResult(false, ex.Message); }
        if (r.Success)
        {
            _restoreHardLimits = false;
            _closingAfterRestore = true;
            // The answer can arrive synchronously (e.g. the demo machine) while WPF is still inside
            // this Closing event; Close() is not allowed then. Close once the event has finished.
            _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
            return;
        }
        DoneButton.IsEnabled = true;
        ResultText.Foreground = Brushes.OrangeRed;
        ResultText.Text = "HÅRDA GRÄNSER ÄR FORTFARANDE AV. " + r.Message;
        _closingAfterRestore = true; // let the user close; the warning is shown
        _ = Dispatcher.BeginInvoke(new Action(() => AppDialog.Show(this, "Kunde inte slå på hårda gränser igen ($21=1):\n\n" + r.Message +
            "\n\nSkriv $21=1 i GRBL-konsolen innan maskinen körs.", "Hårda gränser är av", MessageBoxButton.OK, MessageBoxImage.Error)),
            System.Windows.Threading.DispatcherPriority.Background);
    }
}
