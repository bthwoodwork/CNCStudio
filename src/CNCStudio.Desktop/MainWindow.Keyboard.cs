using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Jogga med tangentbordet (Maskin → Tangentbord…): the keys from Inställningar\tangentbord.json. A jog key
/// works like the jog buttons – a short press is one step, held longer than 250 ms it jogs continuously until
/// released (jog cancel 0x85), and also stops if the window loses focus. Never while typing in a field.
/// </summary>
public partial class MainWindow
{
    private KeyboardShortcuts _keys = AppStorage.Current.LoadKeyboardShortcuts();
    private Key? _keyJogKey;
    private string? _keyJogTag;

    internal static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System or Key.None or Key.ImeProcessed or Key.DeadCharProcessed;

    /// <summary>The key's name, the same for the aliases WPF has (PageDown = Next, Enter = Return).</summary>
    internal static string KeyName(Key k) => k switch
    {
        Key.PageUp => "PageUp", Key.PageDown => "PageDown", Key.Enter => "Enter", Key.CapsLock => "CapsLock",
        Key.Snapshot => "PrintScreen", Key.Back => "Back", _ => k.ToString(),
    };

    internal static KeyChord ChordOf(Key key) => new(KeyName(key),
        Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));

    private bool KeyboardJogActive()
    {
        if (!_keys.Enabled || StartScreenVisible) return false;
        if (_keys.OnlyInMachineWorkspace && _workspace != Workspace.Machine) return false;
        // Typing in a field, or choosing in an open list, keeps the keys.
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem) return false;
        return true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!KeyboardJogActive()) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key) || _keys.Find(ChordOf(key)) is not KeyAction a) return;
        e.Handled = true;                                   // e.g. arrows must not move focus between buttons
        if (e.IsRepeat) return;                             // a held key: the continuous jog is already running
        if (KeyboardShortcuts.JogTag(a) is string tag) StartKeyJog(key, tag);
        else RunKeyAction(a);
    }

    private async void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_keyJogKey != key) return;
        e.Handled = true;
        string tag = _keyJogTag!;
        _keyJogKey = null; _keyJogTag = null;
        _jogHeld = false;
        _jogHoldTimer?.Stop();
        if (_jogContinuous) await StopContinuousJogAsync();
        else await StepJogAsync(tag);
    }

    private async void Window_Deactivated(object? sender, EventArgs e)
    {
        // Another window took the keyboard while a key was held: the key-up never comes here – stop now.
        if (_keyJogKey is null) return;
        _keyJogKey = null; _keyJogTag = null;
        _jogHeld = false;
        _jogHoldTimer?.Stop();
        if (_jogContinuous) await StopContinuousJogAsync();
    }

    private bool CanKeyJog(out string? why)
    {
        why = Grbl is null ? "Anslut maskinen först." : _runner is not null ? "Ett program körs – joggen är avstängd." :
              _macroRunner is not null ? "Ett makro körs – vänta tills det är klart." : null;
        return why is null;
    }

    private void StartKeyJog(Key key, string tag)
    {
        if (_jogHeld) return;                               // already jogging (mouse or another key)
        if (!CanKeyJog(out var why)) { JogStatusText.Text = why; return; }
        _jogHeld = true; _jogContinuous = false;
        _keyJogKey = key; _keyJogTag = tag;
        _jogHoldTimer?.Stop();
        _jogHoldTimer = new DispatcherTimer { Interval = ContinuousJogDelay };
        _jogHoldTimer.Tick += (_, _) =>
        {
            _jogHoldTimer?.Stop();
            if (!_jogHeld || _keyJogTag != tag) return;
            _jogContinuous = true;
            _jogLoop = ContinuousJogAsync(tag);
        };
        _jogHoldTimer.Start();
    }

    private async void RunKeyAction(KeyAction a)
    {
        switch (a)
        {
            case KeyAction.Stop:
                _jogHeld = false; _keyJogKey = null; _keyJogTag = null;
                _jogHoldTimer?.Stop();
                StopPadJog();
                await StopContinuousJogAsync();
                JogStatusText.Text = "Joggen avbröts.";
                return;
            case KeyAction.Pause:
                if (Grbl is GrblClient p) { p.SendRealtime(GrblProtocol.FeedHold); JogStatusText.Text = "Paus (feed hold)."; }
                return;
            case KeyAction.Resume:
                if (Grbl is GrblClient r) { r.SendRealtime(GrblProtocol.CycleStartResume); JogStatusText.Text = "Fortsätter."; }
                return;
            case KeyAction.SpeedPrecise: SetJogSpeed(PresetPrecise); return;
            case KeyAction.SpeedNormal: SetJogSpeed(PresetNormal); return;
            case KeyAction.SpeedRapid: SetJogSpeed(PresetRapid); return;
            case KeyAction.NextSpeed: SetJogSpeed(PresetPrecise.IsChecked == true ? PresetNormal : PresetNormal.IsChecked == true ? PresetRapid : PresetPrecise); return;
        }
        if (!CanKeyJog(out var why)) { JogStatusText.Text = why; return; }
        RunPadAction(a switch
        {
            KeyAction.ZeroXY => GamepadAction.ZeroXY, KeyAction.ZeroZ => GamepadAction.ZeroZ,
            KeyAction.GoToXY0 => GamepadAction.GoToXY0, KeyAction.Home => GamepadAction.Home, _ => GamepadAction.None,
        });
    }

    private void SetJogSpeed(RadioButton preset)
    {
        preset.IsChecked = true;
        JogStatusText.Text = $"Jogghastighet: {preset.Content}.";
    }

    private void ShowKeyboardStatus()
    {
        if (KeyboardStatusText is null) return;
        if (!_keys.Enabled) { KeyboardStatusText.Text = "Tangentbord: avstängt"; return; }
        string K(KeyAction a) => KeyboardShortcuts.Display(_keys.ChordFor(a));
        KeyboardStatusText.Text = $"Tangentbord: X {K(KeyAction.JogXMinus)} {K(KeyAction.JogXPlus)} · Y {K(KeyAction.JogYPlus)} {K(KeyAction.JogYMinus)} · Z {K(KeyAction.JogZPlus)} {K(KeyAction.JogZMinus)} · stopp {K(KeyAction.Stop)}";
    }

    private void KeyboardSettings_Click(object sender, RoutedEventArgs e)
    {
        var w = new KeyboardShortcutsWindow(_keys) { Owner = this };
        if (w.ShowDialog() != true) return;
        _keys = w.Result;
        try { AppStorage.Current.SaveKeyboardShortcuts(_keys); }
        catch (Exception ex) { Error("Tangentbordsinställningarna kunde inte sparas: " + ex.Message); }
        ShowKeyboardStatus();
    }
}
