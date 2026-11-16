using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Spindel och kylning in the Program panel: rapid and spindle overrides next to the feed
/// override, and buttons for the spindle, flood and mist. What each button may do in each state is decided by
/// <see cref="SpindleCoolant"/>; the buttons light up from the machine's own report (A:).
/// </summary>
public partial class MainWindow
{
    private static readonly Brush AccessoryOn = Frozen(Color.FromRgb(0x2E, 0x7A, 0x4A));
    private bool _spindleOn, _floodOn, _mistOn;

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private bool SpindleByGrbl => _document.Machine.Spindle == SpindleControl.Grbl;

    private void ShowSpindleCoolant(GrblStatus s)
    {
        if (s.Overrides is { Length: >= 3 } ov)
        {
            RapidOvText.Text = $"{ov[1]:0} %";
            SpindleOvText.Text = $"{ov[2]:0} %";
        }
        // A: comes with Ov: now and then; between those reports the last known state stands.
        if (s.Accessories is not null)
        {
            _spindleOn = s.SpindleOn == true; _floodOn = s.FloodOn == true; _mistOn = s.MistOn == true;
        }
        SpindleButton.Content = !SpindleByGrbl ? "Spindel (för hand)" : _spindleOn ? "Spindel av" : "Spindel på";
        SpindleButton.Background = _spindleOn ? AccessoryOn : null;
        SpindleButton.IsEnabled = SpindleByGrbl;
        SpindleRpmBox.IsEnabled = SpindleByGrbl && !_spindleOn;
        SpindleOvRow.IsEnabled = SpindleByGrbl;
        FloodButton.Background = _floodOn ? AccessoryOn : null;
        MistButton.Background = _mistOn ? AccessoryOn : null;
        if (s.SpindleSpeed is double rpm && _spindleOn) SpindleButton.ToolTip = $"Spindeln går med {rpm:0} varv/min. Klicka för att stoppa den.";
    }

    private async void SpindleToggle_Click(object sender, RoutedEventArgs e)
    {
        if (Grbl is not GrblClient g) { Error("Anslut maskinen först."); return; }
        bool turnOn = !_spindleOn;
        double rpm = Parse(SpindleRpmBox, out var r) ? r : double.NaN;
        var cmd = SpindleCoolant.Spindle(g.LastStatus, turnOn, rpm, SpindleByGrbl, _runner is not null || _macroRunner is not null);
        if (cmd.Refused is string why) { Error(why); return; }
        if (cmd.Realtime is byte b)
        {
            g.SendRealtime(b);
            StatusText.Text = _spindleOn ? "Spindeln stoppas (pausat). Den startar igen när du klickar Fortsätt." : "Spindeln startar igen.";
            return;
        }
        if (turnOn && AppDialog.Show(this, $"Spindeln startar med {rpm:0} varv/min.\n\nHänder, lösa saker och kablar borta från fräsen?", "Spindel på",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        if (await SendAsync(g, cmd.Line!))
            StatusText.Text = turnOn ? $"Spindeln på ({cmd.Line})." : "Spindeln av (M5).";
    }

    private void Coolant_Click(object sender, RoutedEventArgs e)
    {
        if (Grbl is not GrblClient g) { Error("Anslut maskinen först."); return; }
        bool mist = (sender as Button)?.Tag as string == "mist";
        var cmd = SpindleCoolant.Coolant(g.LastStatus, mist);
        if (cmd.Refused is string why) { Error(why); return; }
        g.SendRealtime(cmd.Realtime!.Value);
        bool wasOn = mist ? _mistOn : _floodOn;
        StatusText.Text = (mist ? "Dimkylning " : "Kylning ") + (wasOn ? "av." : "på.");
    }
}
