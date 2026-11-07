using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CNCStudio.Core.Grbl;

/// <summary>A saved copy of the machine's settings ($$).</summary>
public sealed record GrblBackupFile(string Path, DateTime Time, string Title, string? Reason, string? Computer, string? Firmware,
    IReadOnlyDictionary<int, double> Settings);

/// <summary>One setting that differs between a copy and the machine.</summary>
public sealed record GrblSettingDifference(int Number, string Name, string Unit, double? InCopy, double? InMachine);

/// <summary>
/// Copies of GRBL's settings: written as plain text in the same "$n=value" form GRBL itself prints for $$
/// (so a copy can also be pasted into any GRBL console), with a few comment lines (;) on top. Kept in
/// CNC Studio\Inställningar\GRBL-kopior. The copies the first test made ($$-backup-*.txt in the log
/// folder) are read too.
/// </summary>
public static class GrblBackup
{
    public const string FolderName = "GRBL-kopior";

    public static string Format(IReadOnlyDictionary<int, double> settings, DateTime time, string computer, string? firmware, string? reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; CNC Studio – säkerhetskopia av GRBL-inställningar ($$)");
        sb.AppendLine($"; Datum: {time:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"; Dator: {computer}");
        if (!string.IsNullOrWhiteSpace(firmware)) sb.AppendLine($"; Firmware: {firmware}");
        if (!string.IsNullOrWhiteSpace(reason)) sb.AppendLine($"; Anledning: {reason}");
        foreach (var (n, v) in settings.OrderBy(kv => kv.Key)) sb.AppendLine($"${n}={Value(n, v)}");
        return sb.ToString();
    }

    /// <summary>Writes a copy and returns its path. The file name says when (and why, if given).</summary>
    public static string Save(string folder, IReadOnlyDictionary<int, double> settings, DateTime time, string computer,
        string? firmware, string? reason)
    {
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"$$ {time:yyyy-MM-dd HHmmss}.txt");
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(folder, $"$$ {time:yyyy-MM-dd HHmmss} ({i}).txt");
        AppStorage.WriteAtomic(path, Format(settings, time, computer, firmware, reason));
        return path;
    }

    public static GrblBackupFile? Read(string path)
    {
        string text;
        try { text = File.ReadAllText(path); } catch (Exception) { return null; }
        var settings = new SortedDictionary<int, double>();
        string? reason = null, computer = null, firmware = null;
        DateTime? time = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(';'))
            {
                string Value(string key) => line[(line.IndexOf(':') + 1)..].Trim();
                if (line.StartsWith("; Anledning:")) reason = Value("Anledning");
                else if (line.StartsWith("; Dator:")) computer = Value("Dator");
                else if (line.StartsWith("; Firmware:")) firmware = Value("Firmware");
                else if (line.StartsWith("; Datum:") && DateTime.TryParseExact(Value("Datum"), "yyyy-MM-dd HH:mm:ss",
                             CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) time = t;
                continue;
            }
            if (GrblProtocol.TryParseSetting(line, out var n, out var v)) settings[n] = v;
        }
        if (settings.Count == 0) return null;
        time ??= LogCatalog.StampOf(path) ?? File.GetLastWriteTime(path);
        var title = reason is null ? $"{time:yyyy-MM-dd HH:mm}" : $"{time:yyyy-MM-dd HH:mm} – {reason}";
        return new GrblBackupFile(path, time.Value, title, reason, computer, firmware, settings);
    }

    /// <summary>Every copy in the given folders, newest first.</summary>
    public static IReadOnlyList<GrblBackupFile> List(IEnumerable<string> folders)
    {
        var list = new List<GrblBackupFile>();
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var f in Directory.GetFiles(folder, "$$*.txt"))
                if (Read(f) is { } b) list.Add(b);
        return list.OrderByDescending(b => b.Time).ToList();
    }

    /// <summary>Settings that differ (or exist on only one side), in setting order.</summary>
    public static IReadOnlyList<GrblSettingDifference> Compare(IReadOnlyDictionary<int, double> copy, IReadOnlyDictionary<int, double> machine)
    {
        var list = new List<GrblSettingDifference>();
        foreach (var n in copy.Keys.Union(machine.Keys).OrderBy(k => k))
        {
            double? a = copy.TryGetValue(n, out var av) ? av : null, b = machine.TryGetValue(n, out var bv) ? bv : null;
            if (a is double x && b is double y && Math.Abs(x - y) <= 0.0005) continue;
            var info = GrblSettings.Catalog.TryGetValue(n, out var i) ? i : null;
            list.Add(new GrblSettingDifference(n, info?.Name ?? $"${n}", info?.Unit ?? "", a, b));
        }
        return list;
    }

    /// <summary>What to write to bring the machine back to the copy (only settings the machine has).</summary>
    public static IReadOnlyList<(int Number, double Value)> RestoreChanges(IReadOnlyList<GrblSettingDifference> differences) =>
        differences.Where(d => d.InCopy is not null && d.InMachine is not null).Select(d => (d.Number, d.InCopy!.Value)).ToList();

    public static string Value(int n, double v) =>
        GrblSettings.Catalog.TryGetValue(n, out var info) && info.Kind != GrblSettingKind.Number
            ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.000", CultureInfo.InvariantCulture);
}
