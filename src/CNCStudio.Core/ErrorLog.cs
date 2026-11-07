using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CNCStudio.Core;

/// <summary>One unexpected error: when, what kind, the message, and the full details (stack trace).</summary>
public sealed record ErrorLogEntry(DateTime Time, string Kind, string Message, string Details);

/// <summary>
/// The error log (CNC Studio\Loggar\&lt;dator&gt;\fel.txt). Each entry is a timestamp line followed by the
/// exception text and a blank line. Kept as plain text so it can also be read in any editor.
/// </summary>
public static class ErrorLog
{
    private static readonly Regex Stamp = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", RegexOptions.Compiled);

    /// <summary>This computer's log folder: CNC Studio\Loggar\<datornamn> (synced when a sync folder is used).</summary>
    public static string Folder => AppStorage.Current.LogsFolder;

    public static string FilePath => Path.Combine(Folder, "fel.txt");

    public static string Format(DateTime time, Exception ex) =>
        $"{time:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";

    /// <summary>Entries, newest first.</summary>
    public static IReadOnlyList<ErrorLogEntry> Parse(string text)
    {
        var entries = new List<ErrorLogEntry>();
        DateTime? time = null;
        var body = new List<string>();
        void Flush()
        {
            if (time is null) return;
            var details = string.Join(Environment.NewLine, body).Trim();
            var first = details.Split('\n').FirstOrDefault()?.Trim() ?? "";
            // "System.TimeoutException: Inget svar …" → kind "TimeoutException", message after ": ".
            int colon = first.IndexOf(": ", StringComparison.Ordinal);
            string kind = colon > 0 ? first[..colon] : first;
            string message = colon > 0 ? first[(colon + 2)..] : "";
            kind = kind.Contains('.') ? kind[(kind.LastIndexOf('.') + 1)..] : kind;
            entries.Add(new ErrorLogEntry(time.Value, kind, message, details));
            body.Clear();
        }
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (Stamp.IsMatch(raw.Trim()))
            {
                Flush();
                time = DateTime.ParseExact(raw.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            else if (time is not null) body.Add(raw);
        }
        Flush();
        return entries.OrderByDescending(e => e.Time).ToList();
    }
}
