using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CNCStudio.Core;

public enum LogKind { Error, Machine, FirstTest, SettingsBackup }

/// <summary>One log file of one computer.</summary>
public sealed record LogFileInfo(string Computer, string Path, LogKind Kind, DateTime Time);

/// <summary>
/// Finds the logs of every computer under CNC Studio\Loggar (one subfolder per computer). With a sync
/// folder this shows the logs of all computers that use it, so a run can be read from anywhere.
/// </summary>
public static class LogCatalog
{
    private static readonly (string Pattern, LogKind Kind)[] Patterns =
    {
        (SessionLogFile.Prefix + "*.txt", LogKind.Machine),
        ("forsta-test-*.txt", LogKind.FirstTest),
        ("$$-backup-*.txt", LogKind.SettingsBackup),
    };

    /// <summary>Computers that have a log folder, this computer first (it is always listed).</summary>
    public static IReadOnlyList<string> Computers(string logsRoot, string thisComputer)
    {
        var names = Directory.Exists(logsRoot)
            ? Directory.GetDirectories(logsRoot).Select(d => System.IO.Path.GetFileName(d)!).ToList()
            : new List<string>();
        return names.Where(n => !n.Equals(thisComputer, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Prepend(thisComputer).ToList();
    }

    /// <summary>The computer's logs: its error log (listed even when empty), then the others newest first.</summary>
    public static IReadOnlyList<LogFileInfo> Files(string logsRoot, string computer)
    {
        var folder = System.IO.Path.Combine(logsRoot, computer);
        var list = new List<LogFileInfo> { new(computer, System.IO.Path.Combine(folder, "fel.txt"), LogKind.Error, DateTime.MaxValue) };
        if (!Directory.Exists(folder)) return list;
        foreach (var (pattern, kind) in Patterns)
            foreach (var f in Directory.GetFiles(folder, pattern))
                list.Add(new LogFileInfo(computer, f, kind, StampOf(f) ?? File.GetLastWriteTime(f)));
        return list.OrderBy(f => f.Kind).ThenByDescending(f => f.Time).ToList();
    }

    /// <summary>The "yyyyMMdd-HHmmss" at the end of a log's file name, if it has one.</summary>
    public static DateTime? StampOf(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (name.Length < 15) return null;
        return DateTime.TryParseExact(name[^15..], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : null;
    }
}
