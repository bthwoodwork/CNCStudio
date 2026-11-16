using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CNCStudio.Core.Remote;

/// <summary>
/// What the phone sees: the machine's state, position and the running program – and the latest notice (program done,
/// waiting at M0, stopped, alarm) with a number that grows, so the phone can tell a new one from one it has shown.
/// </summary>
public sealed record RemoteStatus(
    bool Connected, string State, double[]? WorkPos, double[]? MachinePos, bool Homed,
    string? Job, double? JobProgress, string? JobMessage, double[]? Overrides,
    bool SpindleOn, bool FloodOn, IReadOnlyList<string> Locations, string JogSpeed, bool StartAllowed, string Message,
    long NoticeId = 0, string? Notice = null, string? NoticeKind = null);

/// <summary>The machine side: implemented by the Desktop (on its UI thread). A command returns null when done, or why not.</summary>
public interface IRemoteMachine
{
    Task<RemoteStatus> StatusAsync();
    Task<string?> CommandAsync(string action, IReadOnlyDictionary<string, string> args);
}

/// <summary>
/// Styr maskinen från mobilen: a small web server on the computer that runs CNC Studio. A phone
/// on the same network opens the address and gets a page with jog, stop, pause/resume, zero, homing, overrides and
/// saved places. Every request needs the PIN shown on the computer; after five wrong PINs the server waits a minute.
/// Only commands in <see cref="Actions"/> are accepted – nothing that changes settings or sends free G-code.
/// Plain TcpListener: no admin rights or URL reservation needed (Windows asks once to allow it in the firewall).
/// </summary>
public sealed class RemoteServer : IDisposable
{
    public const int DefaultPort = 8765;
    public const int MaxRequestBytes = 16 * 1024;

    /// <summary>Everything the phone may ask for.</summary>
    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(StringComparer.Ordinal)
    {
        "jog", "jogstop", "speed", "stop", "pause", "resume", "home", "unlock", "zero", "goto0", "location",
        "feed", "rapid", "spindleov", "spindle", "flood", "start",
    };

    private readonly IRemoteMachine _machine;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _failures;
    private DateTime _lockedUntil = DateTime.MinValue;
    private readonly object _sync = new();

    public string Pin { get; }
    public int Port { get; private set; }
    public bool Running => _listener is not null;
    /// <summary>Raised (on a worker thread) for each command, for the computer's log: "jog X+", "stop" …</summary>
    public event Action<string>? CommandReceived;

    public RemoteServer(IRemoteMachine machine, string? pin = null)
    {
        _machine = machine;
        Pin = pin ?? NewPin();
    }

    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");

    public void Start(int port = DefaultPort)
    {
        if (_listener is not null) return;
        var l = new TcpListener(IPAddress.Any, port);
        l.Start();
        _listener = l;
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(l, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (Exception) { }
        _listener = null;
    }

    public void Dispose() => Stop();

    /// <summary>The addresses a phone on the same network can open (IPv4, not loopback).</summary>
    public static IReadOnlyList<string> LocalUrls(int port)
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                         n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel))
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                        list.Add($"http://{a.Address}:{port}/");
        }
        catch (Exception) { }
        return list.Distinct().ToList();
    }

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
            var stream = client.GetStream();
            var request = await ReadRequest(stream, ct);
            var (status, type, body) = request is null ? (400, "text/plain", "Felaktig förfrågan") : await Handle(request);
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n" +
                       "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(bytes, ct);
        }
        catch (Exception) { }
    }

    public sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Form);

    private static async Task<Request?> ReadRequest(NetworkStream s, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        int read = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (read >= buffer.Length) return null;
            int n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n <= 0) return null;
            read += n;
            headerEnd = IndexOf(buffer, read, "\r\n\r\n"u8);
        }
        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        int length = 0;
        foreach (var line in head.Split("\r\n").Skip(1))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && !int.TryParse(line[15..].Trim(), out length)) return null;
        if (length < 0 || headerEnd + 4 + length > buffer.Length) return null;
        while (read < headerEnd + 4 + length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n <= 0) return null;
            read += n;
        }
        return Parse(head, Encoding.UTF8.GetString(buffer, headerEnd + 4, length));
    }

    /// <summary>"POST /api/cmd?pin=1 HTTP/1.1" + form body → the request (public for tests).</summary>
    public static Request? Parse(string head, string body)
    {
        var first = head.Split("\r\n")[0].Split(' ');
        if (first.Length < 3) return null;
        var target = first[1];
        int q = target.IndexOf('?');
        string path = q < 0 ? target : target[..q];
        return new Request(first[0], path, Form(q < 0 ? "" : target[(q + 1)..]), Form(body));
    }

    private static Dictionary<string, string> Form(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in s.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int e = pair.IndexOf('=');
            string k = Uri.UnescapeDataString((e < 0 ? pair : pair[..e]).Replace('+', ' '));
            string v = e < 0 ? "" : Uri.UnescapeDataString(pair[(e + 1)..].Replace('+', ' '));
            if (k.Length > 0 && k.Length <= 32 && v.Length <= 64) d[k] = v;
        }
        return d;
    }

    /// <summary>Routing, the PIN check and the command whitelist (public for tests).</summary>
    public async Task<(int Status, string Type, string Body)> Handle(Request r)
    {
        if (r.Method == "GET" && r.Path is "/" or "/index.html") return (200, "text/html", RemotePage.Html);
        if (!r.Path.StartsWith("/api/", StringComparison.Ordinal)) return (404, "text/plain", "Finns inte");
        lock (_sync)
        {
            if (DateTime.UtcNow < _lockedUntil) return (429, "application/json", Json(new { ok = false, error = "För många fel PIN – vänta en minut." }));
        }
        string pin = r.Query.GetValueOrDefault("pin") ?? r.Form.GetValueOrDefault("pin") ?? "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pin.PadRight(6)), Encoding.ASCII.GetBytes(Pin)))
        {
            lock (_sync) { if (++_failures >= 5) { _lockedUntil = DateTime.UtcNow.AddMinutes(1); _failures = 0; } }
            return (401, "application/json", Json(new { ok = false, error = "Fel PIN." }));
        }
        lock (_sync) _failures = 0;
        try
        {
            if (r.Method == "GET" && r.Path == "/api/status") return (200, "application/json", Json(await _machine.StatusAsync()));
            if (r.Method == "POST" && r.Path == "/api/cmd")
            {
                string action = r.Form.GetValueOrDefault("action") ?? "";
                if (!Actions.Contains(action)) return (400, "application/json", Json(new { ok = false, error = "Okänt kommando." }));
                var args = r.Form.Where(kv => kv.Key is not ("pin" or "action")).ToDictionary(kv => kv.Key, kv => kv.Value);
                CommandReceived?.Invoke(action + (args.Count > 0 ? " " + string.Join(" ", args.Values) : ""));
                var error = await _machine.CommandAsync(action, args);
                return (200, "application/json", Json(new { ok = error is null, error }));
            }
        }
        catch (Exception ex) { return (500, "application/json", Json(new { ok = false, error = ex.Message })); }
        return (404, "application/json", Json(new { ok = false, error = "Finns inte." }));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string Json(object o) => JsonSerializer.Serialize(o, JsonOptions);

    private static string Reason(int s) => s switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 429 => "Too Many Requests", _ => "Error" };

    private static int IndexOf(byte[] b, int len, ReadOnlySpan<byte> pattern)
    {
        for (int i = 0; i + pattern.Length <= len; i++)
            if (b.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        return -1;
    }
}
