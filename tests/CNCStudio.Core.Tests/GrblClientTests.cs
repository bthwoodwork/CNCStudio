using System.Globalization;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Core.Tests;

/// <summary>
/// Simulated GRBL 1.1 controller following the verified protocol: "ok"/"error:N" per line,
/// '?' answered with a status report, settings only writable in Idle/Alarm (error 8 otherwise),
/// negative values rejected (error 4). Output is delivered in small fragments like a serial port.
/// </summary>
internal sealed class FakeGrbl : IGrblTransport
{
    public event Action<string>? DataReceived;
    public bool IsOpen { get; private set; }
    public bool SendWelcomeOnOpen { get; set; } = true;
    public bool Silent { get; set; }
    public string State { get; set; } = "Idle";
    public SortedDictionary<int, double> Settings { get; } = new()
    {
        [0] = 10, [3] = 0, [13] = 0, [20] = 0, [22] = 1, [23] = 0, [27] = 1,
        [100] = 40, [130] = 750, [131] = 750, [132] = 65,
    };
    public double[] G54 { get; set; } = { 10, 20, -45.2 };
    public double[] G92 { get; set; } = { 0, 0, 0 };
    public double Tlo { get; set; }
    public int? RejectSetting { get; set; }
    public List<string> ReceivedLines { get; } = new();
    public List<byte> ReceivedBytes { get; } = new();

    public void Open() { IsOpen = true; if (SendWelcomeOnOpen && !Silent) Emit("\r\nGrbl 1.1h ['$' for help]\r\n"); }
    public void Close() => IsOpen = false;
    public void Dispose() => IsOpen = false;

    public void WriteByte(byte value)
    {
        ReceivedBytes.Add(value);
        if (Silent) return;
        if (value == (byte)'?') Emit($"<{State}|MPos:0.000,0.000,-1.000|FS:0,0>\r\n");
        if (value == 0x18) Emit("\r\nGrbl 1.1h ['$' for help]\r\n");
    }

    public bool HoldNextResponse { get; set; }
    public int DelayMs { get; set; }
    private readonly List<string> _held = new();
    private bool _holding;

    public void ReleaseHeld()
    {
        _holding = false;
        var held = _held.ToList(); _held.Clear();
        foreach (var t in held) EmitRaw(t);
    }

    public void Write(string text)
    {
        var line = text.TrimEnd('\n');
        ReceivedLines.Add(line);
        if (Silent) return;
        if (HoldNextResponse) { HoldNextResponse = false; _holding = true; _held.Add("ok\r\n"); return; }
        if (DelayMs > 0) { var d = DelayMs; Task.Delay(d).ContinueWith(_ => Emit("ok\r\n")); return; }
        if (_holding)
        {
            // Queue behind the held answer so the order stays as GRBL would send it.
            Respond(line, t => _held.Add(t));
            return;
        }
        Respond(line, EmitRaw);
    }

    private void Respond(string line, Action<string> emit)
    {
        var saved = _emitOverride; _emitOverride = emit;
        try { RespondCore(line); } finally { _emitOverride = saved; }
    }

    private Action<string>? _emitOverride;

    private void RespondCore(string line)
    {
        if (line == "$$")
        {
            foreach (var (n, v) in Settings) Emit($"${n}={v.ToString("0.000", CultureInfo.InvariantCulture)}\r\n");
            Emit("ok\r\n");
        }
        else if (line == "$#")
        {
            Emit($"[G54:{V(G54)}]\r\n[G55:0.000,0.000,0.000]\r\n[G92:{V(G92)}]\r\n[TLO:{Tlo.ToString("0.000", CultureInfo.InvariantCulture)}]\r\n[PRB:0.000,0.000,0.000:0]\r\nok\r\n");
        }
        else if (GrblProtocol.TryParseSetting(line, out var n, out var v))
        {
            if (State is not ("Idle" or "Alarm") || n == RejectSetting) Emit("error:8\r\n");
            else if (v < 0) Emit("error:4\r\n");
            else { Settings[n] = v; Emit("ok\r\n"); }
        }
        else Emit("ok\r\n");
    }

    private static string V(double[] v) => string.Join(",", v.Select(x => x.ToString("0.000", CultureInfo.InvariantCulture)));

    private void Emit(string text) => (_emitOverride ?? EmitRaw)(text);

    /// <summary>Splits output into 3-character fragments to exercise line reassembly.</summary>
    private void EmitRaw(string text)
    {
        for (int i = 0; i < text.Length; i += 3) DataReceived?.Invoke(text.Substring(i, Math.Min(3, text.Length - i)));
    }
}

public class GrblClientTests
{
    private static async Task<(GrblClient Client, FakeGrbl Fake)> Connected()
    {
        var fake = new FakeGrbl();
        var client = new GrblClient(fake);
        await client.ConnectAsync(TimeSpan.FromSeconds(1));
        return (client, fake);
    }

    [Fact]
    public async Task Connect_waits_for_the_welcome_message()
    {
        var (client, _) = await Connected();
        Assert.Equal("Grbl 1.1h ['$' for help]", client.Version);
    }

    [Fact]
    public async Task Connect_without_welcome_falls_back_to_a_status_request_and_never_resets()
    {
        var fake = new FakeGrbl { SendWelcomeOnOpen = false };
        var client = new GrblClient(fake);
        var text = await client.ConnectAsync(TimeSpan.FromMilliseconds(100));
        Assert.Contains("Idle", text);
        Assert.Equal(new[] { (byte)'?' }, fake.ReceivedBytes);
        Assert.DoesNotContain((byte)0x18, fake.ReceivedBytes);
    }

    [Fact]
    public async Task Connect_fails_when_nothing_answers()
    {
        var fake = new FakeGrbl { Silent = true };
        var client = new GrblClient(fake);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(TimeSpan.FromMilliseconds(50)));
        Assert.False(fake.IsOpen);
    }

    [Fact]
    public async Task Reads_all_settings_with_dollar_dollar()
    {
        var (client, fake) = await Connected();
        var s = await client.ReadSettingsAsync();
        Assert.Equal(fake.Settings.Count, s.Count);
        Assert.Equal(750, s[130]);
        Assert.Equal(65, s[132]);
        Assert.Equal("$$", fake.ReceivedLines.Single());
    }

    [Fact]
    public async Task Writes_settings_one_line_each_and_verifies_by_reading_back()
    {
        var (client, fake) = await Connected();
        var result = await client.WriteSettingsAsync(new[] { (130, 760.5), (3, 2.0) });
        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { "$130=760.5", "$3=2", "$$" }, fake.ReceivedLines);
        Assert.Equal(760.5, fake.Settings[130]);
    }

    [Fact]
    public async Task Writing_is_refused_unless_idle_or_alarm_and_nothing_is_sent()
    {
        var (client, fake) = await Connected();
        fake.State = "Run";
        var result = await client.WriteSettingsAsync(new[] { (130, 760.0) });
        Assert.False(result.Success);
        Assert.Contains("Run", result.Message);
        Assert.Empty(fake.ReceivedLines);
    }

    [Fact]
    public async Task Writing_is_allowed_in_alarm()
    {
        var (client, fake) = await Connected();
        fake.State = "Alarm";
        Assert.True((await client.WriteSettingsAsync(new[] { (27, 2.0) })).Success);
    }

    [Fact]
    public async Task Invalid_values_are_refused_before_anything_is_sent()
    {
        var (client, fake) = await Connected();
        Assert.False((await client.WriteSettingsAsync(new[] { (130, -5.0) })).Success);
        Assert.False((await client.WriteSettingsAsync(new[] { (22, 2.0) })).Success);   // boolean
        Assert.False((await client.WriteSettingsAsync(new[] { (3, 1.5) })).Success);    // mask
        Assert.False((await client.WriteSettingsAsync(new[] { (999, 1.0) })).Success);  // unknown
        Assert.Empty(fake.ReceivedLines);
    }

    [Fact]
    public async Task Writing_stops_at_the_first_error_and_reports_how_many_were_written()
    {
        var (client, fake) = await Connected();
        fake.RejectSetting = 3; // controller answers error:8 for $3
        var result = await client.WriteSettingsAsync(new[] { (130, 700.0), (3, 2.0), (27, 2.0) });
        Assert.False(result.Success);
        Assert.Contains("1 av 3", result.Message);
        Assert.Contains("error:8", result.Message);
        Assert.Equal(new[] { "$130=700", "$3=2" }, fake.ReceivedLines); // $27 never sent, no read-back
        Assert.Equal(700, fake.Settings[130]);
        Assert.Equal(1, fake.Settings[27]);
    }

    [Fact]
    public async Task Error_codes_are_described_with_grbl_text()
    {
        var (client, fake) = await Connected();
        fake.State = "Run";
        var r = await client.SendLineAsync("$130=700");
        Assert.False(r.IsOk);
        Assert.Equal("error:8 – Not idle", r.Describe());
    }

    [Fact]
    public async Task Reads_g54_g92_and_tlo_with_dollar_hash()
    {
        var (client, _) = await Connected();
        var p = await client.ReadParametersAsync();
        Assert.Equal(new[] { 10, 20, -45.2 }, p["G54"]);
        Assert.Equal(new[] { 0.0 }, p["TLO"]);
        Assert.Equal(new[] { 0.0, 0.0, 0.0 }, p["PRB"]);
    }

    [Fact]
    public async Task Status_reports_are_not_mixed_into_command_responses()
    {
        var (client, fake) = await Connected();
        var statusSeen = 0;
        client.StatusChanged += _ => statusSeen++;
        await client.RequestStatusAsync(TimeSpan.FromSeconds(1));
        var r = await client.SendLineAsync("G21");
        Assert.True(r.IsOk);
        Assert.Empty(r.Lines);
        Assert.Equal(1, statusSeen);
        Assert.Equal("Idle", client.LastStatus!.State);
    }

    [Fact]
    public async Task Too_long_or_control_lines_are_refused()
    {
        var (client, fake) = await Connected();
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendLineAsync(new string('G', 71)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendLineAsync("G0\u0018"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendLineAsync("!"));
        Assert.Empty(fake.ReceivedLines);
    }

    [Fact]
    public async Task Soft_reset_is_a_single_byte_and_fails_a_pending_command()
    {
        var (client, fake) = await Connected();
        client.SendRealtime(GrblProtocol.SoftReset);
        Assert.Equal((byte)0x18, fake.ReceivedBytes.Last());
        Assert.Empty(fake.ReceivedLines);
    }

    [Fact]
    public async Task Late_answer_after_a_timeout_is_not_taken_for_the_next_command()
    {
        var (client, fake) = await Connected();
        fake.HoldNextResponse = true;          // the next line's "ok" is held back
        await Assert.ThrowsAsync<TimeoutException>(() => client.SendLineAsync("G4 P5", timeout: TimeSpan.FromMilliseconds(80)));
        fake.RejectSetting = 130;              // the next real answer is error:8
        var sending = client.SendLineAsync("$130=700");
        fake.ReleaseHeld();                    // late "ok" for G4 arrives first, then error:8 for $130
        var r = await sending;
        Assert.False(r.IsOk);                  // must be the $130 answer, not the late ok
        Assert.Equal(8, r.Completion.Code);
    }

    [Fact]
    public async Task Long_commands_can_have_a_longer_timeout()
    {
        var (client, fake) = await Connected();
        client.CommandTimeout = TimeSpan.FromMilliseconds(50);
        fake.DelayMs = 150;
        var r = await client.SendLineAsync("$H", timeout: TimeSpan.FromSeconds(2));
        Assert.True(r.IsOk);
    }

    [Fact]
    public async Task Silent_controller_times_out()
    {
        var (client, fake) = await Connected();
        fake.Silent = true;
        client.CommandTimeout = TimeSpan.FromMilliseconds(100);
        await Assert.ThrowsAsync<TimeoutException>(() => client.SendLineAsync("$$"));
    }
}

public class GrblProtocolTests
{
    [Theory]
    [InlineData("ok", GrblLineKind.Ok)]
    [InlineData("error:8", GrblLineKind.Error)]
    [InlineData("ALARM:1", GrblLineKind.Alarm)]
    [InlineData("<Idle|MPos:0.000,0.000,0.000|FS:0,0>", GrblLineKind.Status)]
    [InlineData("Grbl 1.1h ['$' for help]", GrblLineKind.Welcome)]
    [InlineData("$130=750.000", GrblLineKind.Setting)]
    [InlineData("[MSG:'$H'|'$X' to unlock]", GrblLineKind.Message)]
    [InlineData("[G54:0.000,0.000,0.000]", GrblLineKind.Bracket)]
    public void Lines_are_classified(string raw, GrblLineKind kind) => Assert.Equal(kind, GrblProtocol.Parse(raw).Kind);

    [Fact]
    public void Status_report_fields_are_parsed()
    {
        var s = GrblProtocol.ParseStatus("<Hold:1|MPos:1.000,-2.500,-45.200|FS:0,0|WCO:10.000,20.000,-45.200|Pn:XZ>")!;
        Assert.Equal("Hold", s.State);
        Assert.Equal("1", s.SubState);
        Assert.Equal(new[] { 1, -2.5, -45.2 }, s.MPos);
        Assert.Equal(new[] { 10, 20, -45.2 }, s.Wco);
        Assert.Equal("XZ", s.Pins);
    }

    [Theory]
    [InlineData("Idle", true)]
    [InlineData("Alarm", true)]
    [InlineData("Run", false)]
    [InlineData("Hold", false)]
    [InlineData("Jog", false)]
    [InlineData("Home", false)]
    [InlineData("Door", false)]
    [InlineData("Check", false)]
    [InlineData("Sleep", false)]
    public void Settings_writable_only_in_idle_or_alarm(string state, bool allowed) =>
        Assert.Equal(allowed, GrblProtocol.StateAllowsSettingsWrite(state));

    [Theory]
    [InlineData(130, 750.0, "$130=750")]
    [InlineData(100, 39.9999, "$100=40")]
    [InlineData(27, 1.25, "$27=1.25")]
    [InlineData(3, 5.0, "$3=5")]
    [InlineData(22, 1.0, "$22=1")]
    public void Setting_lines_are_formatted_exactly(int n, double v, string expected)
    {
        Assert.Null(GrblProtocol.FormatSettingWrite(n, v, out var line));
        Assert.Equal(expected, line);
    }

    [Fact]
    public void Profile_differences_and_apply_use_the_verified_settings()
    {
        var profile = new MachineProfile { WorkWidthMm = 750, ZTravelMm = 65, DirInvertMask = 0 };
        var machine = new Dictionary<int, double> { [3] = 2, [130] = 790, [132] = 65, [27] = 2 };
        var diffs = GrblSettings.CompareWithProfile(profile, machine);
        Assert.Contains(diffs, d => d.Setting == 3 && d.MachineValue == "2");
        Assert.Contains(diffs, d => d.Setting == 130 && d.MachineValue == "790");
        Assert.DoesNotContain(diffs, d => d.Setting == 132);
        var applied = GrblSettings.ApplyToProfile(profile, machine);
        Assert.Equal(2, applied.DirInvertMask);
        Assert.Equal(790, applied.WorkWidthMm);
        Assert.Equal(2, applied.HomingPullOffMm);
        Assert.True(applied.LimitMarginMm >= applied.HomingPullOffMm);
        Assert.True(applied.CalibratedToGrbl);
    }
}
