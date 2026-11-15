using System.Windows;
using System.Windows.Controls;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>Sparade platser: a button per place in the machine panel (Fräsbyte, Parkering …), managed in Maskin → Platser….</summary>
public partial class MainWindow
{
    private SavedLocations _locations = new();

    private string? LocationsPath => _document.Machine.Id is string id ? AppStorage.Current.Machines.LocationsPath(id) : null;

    private void RefreshLocations()
    {
        if (LocationsPanel is null) return;
        _locations = LocationsPath is string p ? SavedLocations.Load(p) : new SavedLocations();
        LocationsPanel.Children.Clear();
        foreach (var l in _locations.Locations)
        {
            var b = new Button { Content = l.Name, Tag = l, Margin = new Thickness(2), Padding = new Thickness(8, 3, 8, 3),
                ToolTip = $"Gå till {l.Name}: Z upp, sedan X {l.X:0.#} Y {l.Y:0.#}" + (l.Z is double z ? $", Z ned till {z:0.#}" : ", Z stannar uppe") + " (maskinkoordinater)." };
            b.Click += GoToLocation_Click;
            LocationsPanel.Children.Add(b);
        }
        LocationsHint.Visibility = _locations.Locations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The tool's machine position now: MPos, or WPos + WCO.</summary>
    private double[]? CurrentMachinePosition()
    {
        var st = Grbl?.LastStatus;
        if (st?.MPos is { Length: >= 3 } m) return m;
        if (st?.WPos is { Length: >= 3 } w && _lastWco is { Length: >= 3 } o) return new[] { w[0] + o[0], w[1] + o[1], w[2] + o[2] };
        return null;
    }

    private async void GoToLocation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SavedLocation l) return;
        if (_runner is not null || _macroRunner is not null) { Error("Ett program eller makro körs – vänta tills det är klart."); return; }
        if (RequireIdle("Gå till " + l.Name) is not GrblClient g) return;
        if (!_homedThisSession) { Error("Kör homing först – platserna är i maskinkoordinater, som är kända först efter homing."); return; }
        IReadOnlyList<string> lines;
        try { lines = SavedLocations.GoTo(l, JogLimits); }
        catch (ArgumentException ex) { Error($"{l.Name}: {ex.Message} Ändra platsen under Maskin → Platser…."); return; }
        JogStatusText.Text = $"Går till {l.Name} (Z lyfts först) …";
        foreach (var line in lines)
            if (!await SendAsync(g, line)) return;
        JogStatusText.Text = $"Går till {l.Name}.";
    }

    private void ManageLocations_Click(object sender, RoutedEventArgs e)
    {
        if (LocationsPath is not string path) { Error("Välj en maskin först (Maskin → Maskiner…)."); return; }
        var w = new LocationsWindow(SavedLocations.Load(path), JogLimits, _document.Machine.Name, CurrentMachinePosition) { Owner = this };
        if (w.ShowDialog() != true) return;
        try { w.Result.Save(path); }
        catch (Exception ex) { Error("Platserna kunde inte sparas: " + ex.Message); return; }
        RefreshLocations();
        StatusText.Text = $"{w.Result.Locations.Count} sparade platser för {_document.Machine.Name}.";
    }
}
