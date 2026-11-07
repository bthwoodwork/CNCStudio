using System;
using System.IO;
using System.Text;

namespace CNCStudio.Core;

/// <summary>
/// A machine-control session log on disk: "maskin-yyyyMMdd-HHmmss.txt" in this computer's log folder.
/// Lines are buffered and written in batches (<see cref="Flush"/>), so a sync client such as Google Drive
/// is not asked to upload the file again for every line a job sends.
/// </summary>
public sealed class SessionLogFile : IDisposable
{
    public const string Prefix = "maskin-";
    private readonly object _sync = new();
    private readonly StringBuilder _pending = new();
    private bool _disposed;

    public SessionLogFile(string folder, DateTime started, string header)
    {
        Directory.CreateDirectory(folder);
        FilePath = Path.Combine(folder, $"{Prefix}{started:yyyyMMdd-HHmmss}.txt");
        _pending.AppendLine(header).AppendLine();
        Flush();
    }

    public string FilePath { get; }

    public void Append(string line)
    {
        lock (_sync) { if (!_disposed) _pending.AppendLine(line); }
    }

    /// <summary>Writes the buffered lines. Never throws: a log that cannot be written must not stop the machine.</summary>
    public void Flush()
    {
        string text;
        lock (_sync)
        {
            if (_pending.Length == 0) return;
            text = _pending.ToString();
            _pending.Clear();
        }
        try { File.AppendAllText(FilePath, text, new UTF8Encoding(false)); }
        catch (Exception) { /* Drive offline, disk full … – the in-memory log still has it */ }
    }

    public void Dispose()
    {
        Flush();
        lock (_sync) _disposed = true;
    }
}
