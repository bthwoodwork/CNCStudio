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
        Closed += (_, _) => _grbl.StatusChanged -= OnStatus;
        Closing += OnClosing;
        Loaded += async (_, _) => await ReadSettingsAsync();
        Render();
    }

    private async Task ReadSettingsAsync()
    {
        try
        {
            var s = await _grbl.ReadSettingsAsync();
            int Get(int n) => s.TryGetValue(n, out var v) ? (int)v : -1;
            SettingsText.Text = $"Maskinens inställningar: $5={Get(5)} (inverterade brytaringångar), " +
                $"$21={Get(21)} (hårda gränser), $22={Get(22)} (homing).";
            HardLimitBox.Visibility = Get(21) == 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { SettingsText.Text = "Kunde inte läsa $$: " + ex.Message; }
    }

    private void OnStatus(GrblStatus s) => Dispatcher.BeginInvoke(() =>
    {
        _test.Update(s);
        LiveText.Text = $"Läge {s.State}  ·  Pn: {(s.Pins ?? "(ingen utlöst)")}";
        if (s.State == "Alarm" && HardLimitBox.Visibility == Visibility.Visible && !_restoreHardLimits)
            ResultText.Text = "GRBL är i larm. Om det kom av en brytare (hårda gränser) krävs återställning (Ctrl-X i konsolen).";
        Render();
    });

    private void Render()
    {
        Show('X', LedX, TextX); Show('Y', LedY, TextY); Show('Z', LedZ, TextZ);
        if (_test.ActiveAtStart.Count > 0)
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
        else if (ResultText.Text.Length == 0 || ResultText.Foreground == Brushes.OrangeRed)
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
        led.Background = stuck ? Stuck : state == SwitchTestState.Passed ? Passed : on ? Pressed : Waiting;
        text.Text = stuck ? "utlöst från start!" :
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
        var r = await _grbl.WriteSettingsAsync(new[] { (21, 0.0) });
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
            Close();
            return;
        }
        DoneButton.IsEnabled = true;
        ResultText.Foreground = Brushes.OrangeRed;
        ResultText.Text = "HÅRDA GRÄNSER ÄR FORTFARANDE AV. " + r.Message;
        AppDialog.Show(this, "Kunde inte slå på hårda gränser igen ($21=1):\n\n" + r.Message +
            "\n\nSkriv $21=1 i GRBL-konsolen innan maskinen körs.", "Hårda gränser är av", MessageBoxButton.OK, MessageBoxImage.Error);
        _closingAfterRestore = true; // let the user close; the warning has been shown
    }
}
