using System.Windows;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Fräsbyte i ett flöde: after a program of a multi-tool job, <see cref="ToolChangeWindow"/> takes the user through
/// the change – to the place called Fräsbyte, change the cutter, back over the probe plate where Z was last probed,
/// probe Z (which loads the next program, as Nolla Z does), start.
/// </summary>
public partial class MainWindow
{
    /// <summary>Machine X/Y where Z was last probed (the plate lay there), for going back after a tool change.</summary>
    private (double X, double Y)? _lastProbeXY;
    private ToolChangeWindow? _toolChange;

    /// <summary>The program after the finished one, its tool, and the change window for it.</summary>
    private void OpenToolChange(int done, int next, int count, string tool)
    {
        _toolChange?.Close();
        var change = _locations.Locations.FirstOrDefault(l => l.Name.Contains("fräsbyte", StringComparison.OrdinalIgnoreCase));
        // A Z probe goes straight down: the diameter is only checked for being sensible.
        double diameter = 6;
        var steps = new ToolChangeWindow.Steps(
            GoToChange: change is null ? LiftZToTop : () => MoveTo(change),
            ChangePlace: change?.Name ?? "",
            GoToProbePoint: _lastProbeXY is { } xy ? () => MoveTo(new SavedLocation { Name = "probpunkten", X = xy.X, Y = xy.Y }) : null,
            ProbeZ: () => ProbeZForNext(diameter),
            Start: () =>
            {
                if (_jobLines is null) { Error("Proba Z först – nästa program laddas när Z är satt med den nya fräsen."); return false; }
                StartJob_Click(this, new RoutedEventArgs());
                return true;
            });
        _toolChange = new ToolChangeWindow(done, next, count, tool, _document.Machine.Spindle == SpindleControl.Manual, steps) { Owner = this };
        _toolChange.Closed += (_, _) => _toolChange = null;
        _toolChange.Show();
    }

    /// <summary>Maskin → Fräsbyte…: the change window for the next program of the job, if one is waiting.</summary>
    private void ToolChange_Click(object sender, RoutedEventArgs e)
    {
        if (_nextProgramNumber is not int next || _loadedJob is not { } job)
        { Error("Fräsbytet gäller jobb med flera fräsar: det öppnas när ett program är klart och nästa väntar."); return; }
        string tool = CamTree.Build(_document, _camReport).Programs.FirstOrDefault(p => p.Number == next)?.ToolLabel ?? "nästa fräs";
        OpenToolChange(next - 1, next, job.Count, tool);
    }

    private async Task<string?> MoveTo(SavedLocation place)
    {
        if (_runner is not null || _macroRunner is not null) return "Ett program eller makro körs.";
        if (RequireIdle("Fräsbyte") is not GrblClient g) return "Maskinen är inte ansluten eller står inte still.";
        if (!_homedThisSession) return "Kör homing först – platserna är i maskinkoordinater.";
        IReadOnlyList<string> lines;
        try { lines = SavedLocations.GoTo(place, JogLimits); }
        catch (ArgumentException ex) { return $"{place.Name}: {ex.Message}"; }
        foreach (var line in lines)
            if (!await SendAsync(g, line)) return "Maskinen kunde inte flyttas.";
        JogStatusText.Text = $"Går till {place.Name} (Z lyftes först).";
        return null;
    }

    private async Task<string?> LiftZToTop()
    {
        if (RequireIdle("Fräsbyte") is not GrblClient g) return "Maskinen är inte ansluten eller står inte still.";
        if (!_homedThisSession) return "Kör homing först.";
        return await SendAsync(g, "G53 G0 Z" + (-JogLimits.LimitMarginMm).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)) ? null : "Z kunde inte lyftas.";
    }

    /// <summary>Probe Z with the plate under the new cutter; success sets Z0, which loads and checks the next program.</summary>
    private async Task<(bool Ok, string Message)> ProbeZForNext(double toolDiameter)
    {
        if (_runner is not null || _macroRunner is not null) return (false, "Ett program eller makro körs.");
        if (RequireIdle("Probning") is not GrblClient g) return (false, "Maskinen är inte ansluten eller står inte still.");
        if (!_homedThisSession) return (false, "Kör homing först.");
        var result = await new ProbeCycle(g, AppStorage.Current.LoadProbeSettings()).RunAsync(ProbeKind.Z, toolDiameter);
        if (!result.Success) return (false, result.Message);
        OnProbed(result);
        return _jobLines is not null
            ? (true, $"Z0 satt med den nya fräsen. {_jobName} är laddat och kontrollerat.")
            : (false, "Z är satt, men nästa program kunde inte laddas – se meddelandet i huvudfönstret.");
    }
}
