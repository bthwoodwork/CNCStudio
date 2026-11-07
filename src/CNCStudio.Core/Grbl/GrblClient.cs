using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CNCStudio.Core.Grbl;

/// <summary>Byte transport to the controller (a serial port in the app, a simulator in tests).</summary>
public interface IGrblTransport : IDisposable
{
    /// <summary>Raised with raw text as it arrives; may contain partial lines.</summary>
    event Action<string>? DataReceived;
    bool IsOpen { get; }
    void Open();
    void Close();
    void Write(string text);
    void WriteByte(byte value);
}

public sealed record GrblResponse(IReadOnlyList<GrblLine> Lines, GrblLine Completion)
{
    public bool IsOk => Completion.Kind == GrblLineKind.Ok;
    public string Describe() => GrblSettings.Describe(Completion);
}

public sealed record SettingWriteResult(bool Success, string Message,
    IReadOnlyDictionary<int, double>? VerifiedSettings = null);

/// <summary>
/// GRBL 1.1 client using the simple send-response protocol (the wiki's recommended method, and the
/// only one allowed for settings): one line is sent, then we wait for "ok" or "error:N" before the
/// next. Real-time commands are single bytes outside that queue. Nothing is ever sent automatically
/// except '?' status requests.
/// </summary>
public sealed class GrblClient : IDisposable
{
    private readonly IGrblTransport _transport;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _sync = new();
    private readonly StringBuilder _rx = new();
    private PendingCommand? _pending;
    private TaskCompletionSource<GrblLine>? _welcomeWaiter;
    private TaskCompletionSource<GrblStatus>? _statusWaiter;
    private Timer? _poll;

    private sealed class PendingCommand
    {
        public readonly List<GrblLine> Lines = new();
        public readonly TaskCompletionSource<GrblResponse> Done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public GrblClient(IGrblTransport transport)
    {
        _transport = transport;
        _transport.DataReceived += OnData;
    }

    /// <summary>Every received line, for the console log.</summary>
    public event Action<GrblLine>? LineReceived;
    /// <summary>Every line we send (lines and real-time bytes), for the console log.</summary>
    public event Action<string>? Sent;
    public event Action<GrblStatus>? StatusChanged;

    public GrblStatus? LastStatus { get; private set; }
    public string? Version { get; private set; }
    public bool IsConnected => _transport.IsOpen;

    /// <summary>
    /// True after "[MSG:Reset to continue]": GRBL waits for a soft reset (Ctrl-X) and answers nothing
    /// else (verified: interface.md, protocol.c critical events). Cleared by the welcome after the reset.
    /// </summary>
    public bool ResetRequired { get; private set; }

    public const string ResetRequiredMessage =
        "GRBL väntar på återställning efter ett larm (Reset to continue) och tar inte emot något annat. " +
        "Klicka Återställ (Ctrl-X), sedan homing ($H) – eller $X om du bara ska testa riktningar.";

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Opens the port and waits for GRBL. Whether opening resets the board (DTR) is not confirmed for
    /// every board, so we first wait for the welcome message and otherwise ask for a status report.
    /// We never send a reset on our own.
    /// </summary>
    public async Task<string> ConnectAsync(TimeSpan? welcomeTimeout = null, CancellationToken ct = default)
    {
        var welcome = new TaskCompletionSource<GrblLine>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) { _welcomeWaiter = welcome; _rx.Clear(); }
        _transport.Open();
        var got = await Task.WhenAny(welcome.Task, Task.Delay(welcomeTimeout ?? TimeSpan.FromSeconds(3), ct));
        lock (_sync) _welcomeWaiter = null;
        if (got == welcome.Task) return welcome.Task.Result.Raw;
        var status = await RequestStatusAsync(TimeSpan.FromSeconds(1.5), ct);
        if (status is not null)
        {
            // Many boards do not restart when the port opens, so no welcome was sent. Ask for the
            // build info instead: "$I" answers "[VER:1.1h.20190825:]" (Commands wiki), also in Alarm.
            var ver = await ReadVersionAsync(ct);
            if (ver is not null) Version = "Grbl " + ver;
            return ver is null
                ? $"GRBL svarar (status {status.State}); inget välkomstmeddelande och ingen version från $I."
                : $"Grbl {ver} (läst med $I; status {status.State})";
        }
        _transport.Close();
        throw new InvalidOperationException("Ingen GRBL-kontroller svarade på porten (inget välkomstmeddelande och ingen status).");
    }

    /// <summary>
    /// The firmware version from "$I" ("[VER:1.1h.20190825:]" → "1.1h.20190825"), or null if the
    /// controller does not answer it. Used when no welcome message arrived on connect.
    /// </summary>
    public async Task<string?> ReadVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await SendLineAsync("$I", ct, TimeSpan.FromSeconds(2));
            var line = r.Lines.FirstOrDefault(l => l.Raw.StartsWith("[VER:", StringComparison.Ordinal))?.Raw;
            if (line is null) return null;
            var body = line[5..^1];                                  // "1.1h.20190825:" (build info after ':')
            int colon = body.IndexOf(':');
            var ver = (colon >= 0 ? body[..colon] : body).Trim();
            return ver.Length == 0 ? null : ver;
        }
        catch (Exception) { return null; }
    }

    /// <summary>True if the version text is GRBL 1.1 (welcome "Grbl 1.1h ['$' for help]" or $I "1.1h.20190825").</summary>
    public static bool IsGrbl11(string? version) =>
        version is not null && System.Text.RegularExpressions.Regex.IsMatch(version, @"(^|[^0-9.])1\.1[a-z]?([^0-9]|$)");

    public void Disconnect()
    {
        StopStatusPolling();
        FailPending("Anslutningen stängdes.");
        if (_transport.IsOpen) _transport.Close();
    }

    /// <summary>Sends '?' periodically. A status report is a response to '?', never an "ok".</summary>
    public void StartStatusPolling(TimeSpan interval)
    {
        StopStatusPolling();
        _poll = new Timer(_ => { try { if (_transport.IsOpen) _transport.WriteByte(GrblProtocol.StatusReport); } catch { } },
            null, TimeSpan.Zero, interval);
    }

    public void StopStatusPolling() { _poll?.Dispose(); _poll = null; }

    /// <summary>Asks for one status report and waits for it.</summary>
    public async Task<GrblStatus?> RequestStatusAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<GrblStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _statusWaiter = tcs;
        SendRealtime(GrblProtocol.StatusReport, log: false);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
        lock (_sync) if (_statusWaiter == tcs) _statusWaiter = null;
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    /// <summary>Real-time command: one byte, no newline, no "ok" (config.h / Commands wiki).</summary>
    public void SendRealtime(byte command, bool log = true)
    {
        if (!_transport.IsOpen) throw new InvalidOperationException("Inte ansluten.");
        _transport.WriteByte(command);
        if (log) Sent?.Invoke(command switch
        {
            GrblProtocol.SoftReset => "[Ctrl-X mjuk återställning]",
            GrblProtocol.JogCancel => "[0x85 avbryt jogg]",
            >= 0x90 and <= 0x97 => $"[0x{command:X2} override]",
            _ => ((char)command).ToString()
        });
        if (command == GrblProtocol.SoftReset) FailPending("GRBL återställdes (Ctrl-X).");
    }

    /// <summary>
    /// Sends one line and waits for "ok" or "error:N" (send-response). Some commands only answer
    /// when finished (e.g. $H after the whole homing cycle), so a longer timeout can be given.
    /// </summary>
    public async Task<GrblResponse> SendLineAsync(string line, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (GrblProtocol.CheckCommandLine(line) is string problem) throw new ArgumentException(problem);
        if (!_transport.IsOpen) throw new InvalidOperationException("Inte ansluten.");
        if (ResetRequired) throw new InvalidOperationException(ResetRequiredMessage);
        var limit = timeout ?? CommandTimeout;
        await _sendLock.WaitAsync(ct);
        try
        {
            var pending = new PendingCommand();
            lock (_sync) _pending = pending;
            _transport.Write(line.Trim() + "\n");
            Sent?.Invoke(line.Trim());
            var finished = await Task.WhenAny(pending.Done.Task, Task.Delay(limit, ct));
            if (finished != pending.Done.Task)
            {
                lock (_sync)
                {
                    if (_pending == pending)
                    {
                        _pending = null;
                        // GRBL answers lines strictly in order: this line's late ok/error must not be
                        // taken as the answer to the next line.
                        _lateCompletions++;
                    }
                }
                throw new TimeoutException($"Inget svar från GRBL på \"{line.Trim()}\" inom {limit.TotalSeconds:0} s. " +
                    "Svaret ignoreras om det kommer senare.");
            }
            return await pending.Done.Task;
        }
        finally { _sendLock.Release(); }
    }

    private int _lateCompletions;

    /// <summary>Reads the whole settings memory with $$.</summary>
    public async Task<IReadOnlyDictionary<int, double>> ReadSettingsAsync(CancellationToken ct = default)
    {
        var r = await SendLineAsync("$$", ct);
        if (!r.IsOk) throw new InvalidOperationException("$$ misslyckades: " + r.Describe());
        var settings = new SortedDictionary<int, double>();
        foreach (var l in r.Lines)
            if (GrblProtocol.TryParseSetting(l.Raw, out var n, out var v)) settings[n] = v;
        if (settings.Count == 0) throw new InvalidOperationException("$$ gav inga inställningar.");
        return settings;
    }

    /// <summary>Reads coordinate parameters with $# ([G54:x,y,z], [G92:…], [TLO:z] …).</summary>
    public async Task<IReadOnlyDictionary<string, double[]>> ReadParametersAsync(CancellationToken ct = default)
    {
        var r = await SendLineAsync("$#", ct);
        if (!r.IsOk) throw new InvalidOperationException("$# misslyckades: " + r.Describe());
        var result = new Dictionary<string, double[]>();
        foreach (var l in r.Lines)
            if (GrblProtocol.TryParseParameter(l.Raw, out var name, out var values)) result[name] = values;
        return result;
    }

    /// <summary>
    /// Writes settings one at a time (send-response; each goes to EEPROM at once). Refused unless the
    /// controller reports Idle or Alarm. Stops at the first error, then reads $$ back and checks
    /// every written value.
    /// </summary>
    /// <summary>
    /// Called with the machine's settings just before WriteSettingsAsync changes any (and with what will
    /// change). The app saves a copy there, so every change made from CNC Studio can be undone.
    /// </summary>
    public static Action<IReadOnlyDictionary<int, double>, IReadOnlyList<(int Number, double Value)>>? BeforeSettingsWrite { get; set; }

    public async Task<SettingWriteResult> WriteSettingsAsync(IReadOnlyList<(int Number, double Value)> changes,
        CancellationToken ct = default)
    {
        if (changes.Count == 0) return new SettingWriteResult(true, "Inga ändringar.");
        var lines = new List<(int Number, double Value, string Line)>();
        foreach (var (n, v) in changes)
        {
            if (GrblProtocol.FormatSettingWrite(n, v, out var line) is string error)
                return new SettingWriteResult(false, error);
            lines.Add((n, v, line));
        }
        var status = await RequestStatusAsync(TimeSpan.FromSeconds(2), ct);
        if (status is null) return new SettingWriteResult(false, "Fick ingen status från maskinen – inget skrevs.");
        if (!GrblProtocol.StateAllowsSettingsWrite(status.State))
            return new SettingWriteResult(false, $"Maskinen är i läge \"{status.State}\". Inställningar kan bara skrivas i Idle eller Alarm – inget skrevs.");

        // A copy of every setting as it is now, before anything changes (so each change can be undone).
        if (BeforeSettingsWrite is { } backup)
        {
            try { backup(await ReadSettingsAsync(ct), changes); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* a failed copy must not stop the write */ }
        }
        int written = 0;
        foreach (var (n, _, line) in lines)
        {
            var r = await SendLineAsync(line, ct);
            if (!r.IsOk)
                return new SettingWriteResult(false,
                    $"{line} avvisades: {r.Describe()}. {written} av {lines.Count} inställningar hann skrivas; resten skickades inte.");
            written++;
        }
        var readBack = await ReadSettingsAsync(ct);
        var mismatches = lines
            .Where(l => !readBack.TryGetValue(l.Number, out var actual) || Math.Abs(actual - l.Value) > 0.0005)
            .Select(l => $"${l.Number} (skrev {l.Value}, läste {(readBack.TryGetValue(l.Number, out var a) ? a.ToString() : "saknas")})")
            .ToList();
        return mismatches.Count == 0
            ? new SettingWriteResult(true, $"{written} inställningar skrevs och kontrollerades mot $$.", readBack)
            : new SettingWriteResult(false, "Kontrolläsningen stämmer inte: " + string.Join(", ", mismatches), readBack);
    }

    private void OnData(string chunk)
    {
        List<string> complete = new();
        lock (_sync)
        {
            _rx.Append(chunk);
            var text = _rx.ToString();
            int nl;
            while ((nl = text.IndexOf('\n')) >= 0)
            {
                complete.Add(text[..nl].TrimEnd('\r'));
                text = text[(nl + 1)..];
            }
            _rx.Clear().Append(text);
        }
        foreach (var raw in complete) Handle(GrblProtocol.Parse(raw));
    }

    private void Handle(GrblLine line)
    {
        if (line.Kind == GrblLineKind.Empty) return;
        if (line.Kind == GrblLineKind.Status)
        {
            var status = GrblProtocol.ParseStatus(line.Raw);
            if (status is null) return;
            LastStatus = status;
            TaskCompletionSource<GrblStatus>? waiter;
            lock (_sync) { waiter = _statusWaiter; _statusWaiter = null; }
            waiter?.TrySetResult(status);
            StatusChanged?.Invoke(status);
            return; // status is a reply to '?', never part of a command response
        }
        LineReceived?.Invoke(line);
        if (line.Kind == GrblLineKind.Welcome)
        {
            Version = line.Raw;
            TaskCompletionSource<GrblLine>? w;
            lock (_sync) { w = _welcomeWaiter; _welcomeWaiter = null; }
            w?.TrySetResult(line);
            lock (_sync) _lateCompletions = 0; // a reset clears GRBL's buffers: nothing more will be answered
            ResetRequired = false;
            FailPending("GRBL startade om (välkomstmeddelande mottogs).");
            return;
        }
        if (line.Kind == GrblLineKind.Message && line.Raw.Contains("Reset to continue", StringComparison.OrdinalIgnoreCase))
        {
            // After a critical alarm (e.g. a limit switch with $21=1) GRBL ignores everything except a
            // soft reset (Ctrl-X): do not send lines that can never be answered.
            ResetRequired = true;
            FailPending(ResetRequiredMessage);
            return;
        }
        if (line.Kind == GrblLineKind.Alarm)
        {
            // After a hard/soft limit or homing alarm GRBL waits for a reset and never answers the
            // line in progress (protocol.c critical event) – fail it instead of waiting forever.
            FailPending("GRBL gick i larm: " + GrblSettings.Describe(line));
            return;
        }
        PendingCommand? p;
        lock (_sync)
        {
            if (line.IsCompletion && _lateCompletions > 0) { _lateCompletions--; return; } // belongs to a timed-out line
            p = _pending;
        }
        if (p is null) return;
        if (line.IsCompletion)
        {
            lock (_sync) if (_pending == p) _pending = null;
            p.Done.TrySetResult(new GrblResponse(p.Lines.ToList(), line));
        }
        else p.Lines.Add(line);
    }

    private void FailPending(string reason)
    {
        PendingCommand? p;
        lock (_sync) { p = _pending; _pending = null; }
        p?.Done.TrySetException(new InvalidOperationException(reason));
    }

    public void Dispose()
    {
        StopStatusPolling();
        _transport.DataReceived -= OnData;
        _transport.Dispose();
        _sendLock.Dispose();
    }
}
