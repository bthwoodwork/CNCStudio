using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CNCStudio.Core.Grbl;

public enum JobOutcome { Finished, Stopped, Failed }

public sealed record JobResult(JobOutcome Outcome, int AckedLines, string Message);

/// <summary>
/// Streams a program to GRBL with the simple send-response protocol (verified: the most reliable
/// method; each line waits for "ok"/"error:N"). After every "ok" the progress is stored on disk so
/// an interrupted job can be resumed (see <see cref="ResumePlanner"/>).
/// <list type="bullet">
/// <item>"ok" may take long: it comes only when the planner has room, and after M0 only when the
/// user resumes with ~ (gcode.c/protocol.c). Lines therefore wait without a timeout.</item>
/// <item>An "error:N" stops the job: feed hold, and the user decides.</item>
/// <item>Stop = feed hold (!), wait for Hold:0 or Idle, then soft reset (0x18): position is kept
/// because the machine is not moving (commands.md: "If reset while in not motion, position is retained").</item>
/// </list>
/// </summary>
public sealed class JobRunner
{
    private readonly GrblClient _client;
    private readonly IReadOnlyList<string> _lines;
    private readonly JobProgressStore? _store;
    private volatile bool _stopRequested;

    public JobRunner(GrblClient client, IReadOnlyList<string> lines, JobProgressStore? store)
    {
        _client = client; _lines = lines; _store = store;
    }

    public int TotalLines => _lines.Count;
    /// <summary>Program lines answered with ok (index of the next line).</summary>
    public int AckedLines { get; private set; }
    public bool IsRunning { get; private set; }
    public string? CurrentLine { get; private set; }
    public event Action? ProgressChanged;

    /// <summary>
    /// Runs the program from <paramref name="startIndex"/>, after sending <paramref name="preamble"/>
    /// (used when resuming). Returns when the last line is answered, on error, or when stopped.
    /// </summary>
    public async Task<JobResult> RunAsync(int startIndex = 0, IReadOnlyList<string>? preamble = null, CancellationToken ct = default)
    {
        IsRunning = true;
        AckedLines = startIndex;
        try
        {
            foreach (var line in preamble ?? Array.Empty<string>())
            {
                var code = ResumePlanner.Strip(line);
                if (code.Length == 0) continue;
                CurrentLine = "Förstart: " + line;
                ProgressChanged?.Invoke();
                var r = await _client.SendLineAsync(code, ct, Timeout.InfiniteTimeSpan);
                if (!r.IsOk) return Fail($"Förstartsraden \"{line}\" avvisades: {r.Describe()}");
                if (_stopRequested) return Done(JobOutcome.Stopped, "Stoppad.");
            }
            for (int i = startIndex; i < _lines.Count; i++)
            {
                if (_stopRequested) return Done(JobOutcome.Stopped, "Stoppad.");
                var code = ResumePlanner.Strip(_lines[i]);
                CurrentLine = _lines[i];
                if (code.Length > 0)
                {
                    var r = await _client.SendLineAsync(code, ct, Timeout.InfiniteTimeSpan);
                    if (!r.IsOk)
                    {
                        try { _client.SendRealtime(GrblProtocol.FeedHold); } catch (Exception) { }
                        return Fail($"Rad {i + 1} \"{_lines[i].Trim()}\" avvisades: {r.Describe()}. Maskinen har pausats (feed hold).");
                    }
                }
                AckedLines = i + 1;
                _store?.Update(AckedLines);
                ProgressChanged?.Invoke();
            }
            // Every line is queued, but the machine may still be cutting the last ones: wait for Idle.
            CurrentLine = "Väntar på att de sista rörelserna blir klara …";
            ProgressChanged?.Invoke();
            while (!_stopRequested)
            {
                var s = await _client.RequestStatusAsync(TimeSpan.FromSeconds(2), ct);
                if (s?.State == "Idle") break;
                if (s?.State == "Alarm") return Fail("Maskinen gick i larm i slutet av programmet.");
                await Task.Delay(200, ct);
            }
            if (_stopRequested) return Done(JobOutcome.Stopped, "Stoppad.");
            return Done(JobOutcome.Finished, "Programmet är klart.");
        }
        catch (Exception ex)
        {
            if (_stopRequested) return Done(JobOutcome.Stopped, "Stoppad.");
            return Fail("Körningen avbröts: " + ex.Message);
        }
        finally { IsRunning = false; ProgressChanged?.Invoke(); }
    }

    public void Pause() => _client.SendRealtime(GrblProtocol.FeedHold);
    public void Resume() => _client.SendRealtime(GrblProtocol.CycleStartResume);

    /// <summary>Feed hold, wait until stopped (Hold:0 or Idle), then soft reset. Position is kept.</summary>
    public async Task StopAsync()
    {
        _stopRequested = true;
        if (!_client.IsConnected) return;
        _client.SendRealtime(GrblProtocol.FeedHold);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < until)
        {
            var s = await _client.RequestStatusAsync(TimeSpan.FromSeconds(1));
            if (s is not null && (s.State == "Idle" || s.State == "Alarm" || (s.State == "Hold" && s.SubState == "0"))) break;
            await Task.Delay(100);
        }
        _client.SendRealtime(GrblProtocol.SoftReset);
    }

    private JobResult Done(JobOutcome outcome, string message)
    {
        _store?.Update(AckedLines, force: true);
        _store?.Finish(outcome == JobOutcome.Finished ? "Finished" : outcome == JobOutcome.Stopped ? "Stopped" : "Failed", message);
        return new JobResult(outcome, AckedLines, message);
    }

    private JobResult Fail(string message) => Done(JobOutcome.Failed, message);

    /// <summary>
    /// $C check mode: GRBL parses every line but moves nothing (commands.md). Turning $C off again
    /// makes GRBL soft-reset; in Idle that keeps the position. Returns the errors found.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(GrblClient client, IReadOnlyList<string> lines,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var on = await client.SendLineAsync("$C", ct);
        if (!on.IsOk) return new[] { "$C avvisades: " + on.Describe() };
        try
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var code = ResumePlanner.Strip(lines[i]);
                if (code.Length == 0) continue;
                var r = await client.SendLineAsync(code, ct, TimeSpan.FromSeconds(30));
                if (!r.IsOk) errors.Add($"Rad {i + 1} \"{lines[i].Trim()}\": {r.Describe()}");
                if (i % 50 == 0) progress?.Report(i);
            }
        }
        finally
        {
            try { await client.SendLineAsync("$C", CancellationToken.None, TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* GRBL resets right after answering – that is expected */ }
        }
        progress?.Report(lines.Count);
        return errors;
    }
}
