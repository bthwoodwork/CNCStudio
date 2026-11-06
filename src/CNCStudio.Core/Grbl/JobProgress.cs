using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CNCStudio.Core.Grbl;

/// <summary>How far a job has come; written to disk while it runs so it survives a power cut.</summary>
public sealed class JobProgress
{
    public string Name { get; set; } = "";
    public string GCodeHash { get; set; } = "";
    public int TotalLines { get; set; }
    /// <summary>Lines GRBL has answered "ok" to (index of the next line to send). "ok" means queued, not cut.</summary>
    public int AckedLines { get; set; }
    public int StartedFromLine { get; set; }
    public DateTime Started { get; set; }
    public DateTime Updated { get; set; }
    /// <summary>Running, Finished, Stopped, Failed.</summary>
    public string State { get; set; } = "Running";
    public string? Message { get; set; }
    /// <summary>G54 when the job started ($#). Compared before resuming: a re-zeroed machine would cut elsewhere.</summary>
    public double[]? G54 { get; set; }

    public bool Unfinished => State is "Running" or "Failed";
}

/// <summary>
/// Keeps a copy of the running program and its progress in a folder (%APPDATA%\CNCStudio\job).
/// Every write goes to a temporary file that then replaces the old one, so a power cut leaves either
/// the old or the new progress – never a half-written file.
/// </summary>
public sealed class JobProgressStore
{
    private readonly string _folder;
    private JobProgress? _current;
    private DateTime _lastWrite;

    public JobProgressStore(string folder) { _folder = folder; }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CNCStudio", "job");

    public string ProgramPath => Path.Combine(_folder, "current-job.nc");
    public string ProgressPath => Path.Combine(_folder, "current-job.json");
    public JobProgress? Current => _current;

    /// <summary>Throttle for progress writes; the last position is always flushed on Finish.</summary>
    public TimeSpan WriteInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public static string Hash(IReadOnlyList<string> lines)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexString(bytes);
    }

    public void Begin(string name, IReadOnlyList<string> lines, int startLine, double[]? g54)
    {
        Directory.CreateDirectory(_folder);
        string hash = Hash(lines);
        bool sameProgram = File.Exists(ProgramPath) && Load() is { } old && old.GCodeHash == hash;
        if (!sameProgram) WriteAtomic(ProgramPath, string.Join("\r\n", lines) + "\r\n");
        _current = new JobProgress
        {
            Name = name, GCodeHash = hash, TotalLines = lines.Count, AckedLines = startLine,
            StartedFromLine = startLine, Started = DateTime.Now, Updated = DateTime.Now, G54 = g54, State = "Running"
        };
        Save(force: true);
    }

    public void Update(int ackedLines, bool force = false)
    {
        if (_current is null) return;
        _current.AckedLines = ackedLines;
        Save(force);
    }

    public void Finish(string state, string? message = null)
    {
        if (_current is null) return;
        _current.State = state;
        _current.Message = message;
        Save(force: true);
    }

    /// <summary>The saved job, or null if there is none or the files do not match.</summary>
    public (JobProgress Progress, string[] Lines)? LoadJob()
    {
        var p = Load();
        if (p is null || !File.Exists(ProgramPath)) return null;
        var lines = File.ReadAllLines(ProgramPath, Encoding.ASCII);
        if (Hash(lines) != p.GCodeHash || lines.Length != p.TotalLines) return null;
        return (p, lines);
    }

    public JobProgress? Load()
    {
        try { return File.Exists(ProgressPath) ? JsonSerializer.Deserialize<JobProgress>(File.ReadAllText(ProgressPath)) : null; }
        catch (Exception) { return null; }
    }

    public void Clear()
    {
        _current = null;
        foreach (var f in new[] { ProgressPath, ProgramPath }) if (File.Exists(f)) File.Delete(f);
    }

    private void Save(bool force)
    {
        if (_current is null) return;
        var now = DateTime.Now;
        if (!force && now - _lastWrite < WriteInterval) return;
        _current.Updated = now;
        _lastWrite = now;
        WriteAtomic(ProgressPath, JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);                   // survive a power cut, not just a crash
        }
        File.Move(tmp, path, overwrite: true);
    }
}
