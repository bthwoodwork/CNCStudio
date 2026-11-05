using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core.Grbl;

public enum SwitchTestState { Waiting, Pressed, Passed }

/// <summary>
/// Manual limit-switch test without any motion: the user presses each switch by hand and the
/// status report's Pn field is watched. Verified (report.c, limits.c): Pn lists triggered pins with
/// $5 already applied; letters X, Y, Z are the limit pins; the field is absent when nothing is
/// triggered. An axis passes only when its switch is seen pressed AND released again.
/// </summary>
public sealed class LimitSwitchTest
{
    public static readonly char[] Axes = { 'X', 'Y', 'Z' };

    private readonly Dictionary<char, SwitchTestState> _state = Axes.ToDictionary(a => a, _ => SwitchTestState.Waiting);
    private bool _first = true;

    /// <summary>Axes that already read as triggered in the first report, before anything was pressed.</summary>
    public IReadOnlyCollection<char> ActiveAtStart { get; private set; } = Array.Empty<char>();
    public IReadOnlyDictionary<char, SwitchTestState> States => _state;
    public IReadOnlyCollection<char> CurrentlyTriggered { get; private set; } = Array.Empty<char>();
    public bool AllPassed => _state.Values.All(s => s == SwitchTestState.Passed);

    /// <summary>Limit axes in a Pn field (P = probe and D/R/H/S = control pins are ignored).</summary>
    public static IReadOnlyCollection<char> LimitAxes(string? pins) =>
        pins is null ? Array.Empty<char>() : Axes.Where(pins.Contains).ToArray();

    /// <summary>Feeds one status report. Returns true if anything changed.</summary>
    public bool Update(GrblStatus status)
    {
        var active = LimitAxes(status.Pins);
        bool changed = !active.SequenceEqual(CurrentlyTriggered);
        CurrentlyTriggered = active;
        if (_first)
        {
            _first = false;
            ActiveAtStart = active;
            return true; // a switch active before the user touched anything is not a press
        }
        foreach (var axis in Axes)
        {
            bool on = active.Contains(axis);
            var before = _state[axis];
            if (before == SwitchTestState.Waiting && on && !ActiveAtStart.Contains(axis)) _state[axis] = SwitchTestState.Pressed;
            else if (before == SwitchTestState.Pressed && !on) _state[axis] = SwitchTestState.Passed;
            changed |= before != _state[axis];
        }
        // A switch that was stuck at start and is now released can be tested normally.
        if (ActiveAtStart.Count > 0 && ActiveAtStart.Any(a => !active.Contains(a)))
            ActiveAtStart = ActiveAtStart.Where(active.Contains).ToArray();
        return changed;
    }

    public void Reset()
    {
        foreach (var a in Axes) _state[a] = SwitchTestState.Waiting;
        _first = true;
        ActiveAtStart = Array.Empty<char>();
        CurrentlyTriggered = Array.Empty<char>();
    }
}
