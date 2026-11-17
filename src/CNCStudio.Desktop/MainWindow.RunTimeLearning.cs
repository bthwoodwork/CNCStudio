using CNCStudio.Core;
using CNCStudio.Core.GCode;

namespace CNCStudio.Desktop;

/// <summary>
/// Tidsuppskattning som lär sig: each run is logged with its estimate and the time the machine stood still
/// in Hold (pauses, M0); the machine log then gives a factor that the estimates are shown corrected by.
/// </summary>
public partial class MainWindow
{
    /// <summary>The uncorrected estimate of the loaded program, logged with the run.</summary>
    private double? _loadedEstimate;
    private DateTime? _holdSince;
    private double _heldMinutes;
    private (string? Machine, RunTimeFactor? Factor)? _learned;

    /// <summary>The estimate of one program of the project (its own motion plan).</summary>
    private double EstimateProgram(GCodeJob job) =>
        RunTimeEstimator.Estimate(_document, new CamReport(job.Operations) { Motion = job.Motion }).TotalMinutes;

    /// <summary>Called with every status during a run: adds up the time spent in Hold or Door.</summary>
    private void TrackHold(string state)
    {
        bool held = _runner is not null && state is "Hold" or "Door";
        if (held) _holdSince ??= DateTime.Now;
        else if (_holdSince is DateTime since) { _heldMinutes += (DateTime.Now - since).TotalMinutes; _holdSince = null; }
    }

    private void BeginHoldTracking() { _holdSince = null; _heldMinutes = 0; }

    private double EndHoldTracking()
    {
        if (_holdSince is DateTime since) _heldMinutes += (DateTime.Now - since).TotalMinutes;
        _holdSince = null;
        return _heldMinutes;
    }

    /// <summary>What this machine has learned from its runs, or null (no machine, or too few runs yet).</summary>
    private RunTimeFactor? LearnedRunTime()
    {
        var id = _document.Machine.Id;
        if (_learned is { } c && c.Machine == id) return c.Factor;
        RunTimeFactor? f = null;
        if (id is not null)
            try { f = RunTimeLearning.Learn(MachineLog.Load(AppStorage.Current.Machines.MachineLogPath(id)).Jobs); }
            catch (Exception) { }
        _learned = (id, f);
        return f;
    }

    /// <summary>" Efter n körningar på maskinen: … (+x %)." or "".</summary>
    private string LearnedText(double estimatedMinutes) =>
        estimatedMinutes > 0 && LearnedRunTime() is { } f ? " " + RunTimeLearning.Describe(f, estimatedMinutes) + "." : "";
}
