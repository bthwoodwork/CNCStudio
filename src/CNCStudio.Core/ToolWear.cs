using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// Körtid per fräs: after a run, its time is added to the tools of the program (by name, as the program knows them);
/// a tool whose change interval has run out is reported, once, when it gets there.
/// </summary>
public static class ToolWear
{
    /// <summary>Adds <paramref name="minutes"/> to each named tool; returns the tools that became due with this run.</summary>
    public static IReadOnlyList<ToolDefinition> Record(ToolLibrary library, IEnumerable<string?> toolNames, double minutes)
    {
        var due = new List<ToolDefinition>();
        if (!(minutes > 0) || !double.IsFinite(minutes)) return due;
        foreach (var name in toolNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tool = library.Tools.FirstOrDefault(t => string.Equals(t.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (tool is null) continue;
            bool wasDue = tool.ChangeDue;
            tool.RunMinutes += minutes;
            tool.RunMinutesSinceChange += minutes;
            if (tool.ChangeDue && !wasDue) due.Add(tool);
        }
        return due;
    }

    /// <summary>"12 h 30 min", "45 min".</summary>
    public static string Hours(double minutes)
    {
        int m = (int)Math.Round(minutes);
        return m < 60 ? $"{m} min" : $"{m / 60} h {m % 60} min";
    }
}
