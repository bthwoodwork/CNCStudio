using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNCStudio.Core.Grbl;

/// <summary>
/// GRBL 1.1 text protocol. Every rule here is verified against the GRBL 1.1 source or wiki;
/// see docs/GRBL-verifierat.md. Pure parsing – no I/O – so it can be tested without a machine.
/// </summary>
public static class GrblProtocol
{
    /// <summary>config.h: BAUD_RATE 115200; wiki: 8-N-1.</summary>
    public const int BaudRate = 115200;

    /// <summary>
    /// GRBL's line buffer is 80 characters (protocol.h default, config.h comment). We stay well below
    /// it so a line can never overflow (error 11/14).
    /// </summary>
    public const int MaxLineLength = 70;

    // Real-time commands (config.h): no newline, not part of the streaming protocol, no "ok".
    public const byte StatusReport = (byte)'?';
    public const byte CycleStartResume = (byte)'~';
    public const byte FeedHold = (byte)'!';
    public const byte SoftReset = 0x18;
    public const byte JogCancel = 0x85;

    // Feed override (config.h CMD_FEED_OVR_*): 10–200 %, coarse 10 %, fine 1 %.
    public const byte FeedOverrideReset = 0x90;
    public const byte FeedOverrideCoarsePlus = 0x91;
    public const byte FeedOverrideCoarseMinus = 0x92;
    public const byte FeedOverrideFinePlus = 0x93;
    public const byte FeedOverrideFineMinus = 0x94;
    // Rapid override: only 100 / 50 / 25 % (config.h CMD_RAPID_OVR_*).
    public const byte RapidOverrideReset = 0x95;
    public const byte RapidOverrideMedium = 0x96;
    public const byte RapidOverrideLow = 0x97;

    public static GrblLine Parse(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0) return new GrblLine(GrblLineKind.Empty, line);
        if (line == "ok") return new GrblLine(GrblLineKind.Ok, line);
        if (line.StartsWith("error:", StringComparison.Ordinal))
            return new GrblLine(GrblLineKind.Error, line, Code(line, 6));
        if (line.StartsWith("ALARM:", StringComparison.Ordinal))
            return new GrblLine(GrblLineKind.Alarm, line, Code(line, 6));
        if (line.StartsWith("<", StringComparison.Ordinal) && line.EndsWith(">", StringComparison.Ordinal))
            return new GrblLine(GrblLineKind.Status, line);
        if (line.StartsWith("Grbl ", StringComparison.Ordinal))
            return new GrblLine(GrblLineKind.Welcome, line);
        if (line.StartsWith("$", StringComparison.Ordinal) && TryParseSetting(line, out _, out _))
            return new GrblLine(GrblLineKind.Setting, line);
        if (line.StartsWith("[MSG:", StringComparison.Ordinal))
            return new GrblLine(GrblLineKind.Message, line);
        if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            return new GrblLine(GrblLineKind.Bracket, line);
        return new GrblLine(GrblLineKind.Other, line);
    }

    private static int? Code(string line, int start) =>
        int.TryParse(line[start..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : null;

    /// <summary>"$130=750.000" → (130, 750). report.c prints $N=value.</summary>
    public static bool TryParseSetting(string line, out int number, out double value)
    {
        number = 0; value = 0;
        var t = line.Trim();
        int eq = t.IndexOf('=');
        if (!t.StartsWith("$", StringComparison.Ordinal) || eq < 2) return false;
        return int.TryParse(t[1..eq], NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
               double.TryParse(t[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>"[G54:0.000,0.000,-45.200]" → ("G54", [0, 0, −45.2]). $# output (report.c).</summary>
    public static bool TryParseParameter(string line, out string name, out double[] values)
    {
        name = ""; values = Array.Empty<double>();
        var t = line.Trim();
        if (!t.StartsWith("[", StringComparison.Ordinal) || !t.EndsWith("]", StringComparison.Ordinal)) return false;
        int colon = t.IndexOf(':');
        if (colon < 2) return false;
        name = t[1..colon];
        var body = t[(colon + 1)..^1];
        int probeFlag = body.IndexOf(':'); // [PRB:x,y,z:s]
        if (probeFlag >= 0) body = body[..probeFlag];
        var parts = body.Split(',');
        var list = new List<double>();
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
            list.Add(v);
        }
        values = list.ToArray();
        return true;
    }

    /// <summary>Parses a 1.1 status report: &lt;State|MPos:x,y,z|...&gt; (report.c).</summary>
    public static GrblStatus? ParseStatus(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith("<", StringComparison.Ordinal) || !t.EndsWith(">", StringComparison.Ordinal)) return null;
        var fields = t[1..^1].Split('|');
        if (fields.Length == 0 || fields[0].Length == 0) return null;
        var stateParts = fields[0].Split(':');
        var status = new GrblStatus { State = stateParts[0], SubState = stateParts.Length > 1 ? stateParts[1] : null };
        foreach (var f in fields.Skip(1))
        {
            int c = f.IndexOf(':');
            if (c < 0) continue;
            string key = f[..c], val = f[(c + 1)..];
            switch (key)
            {
                case "MPos": status.MPos = Vec(val); break;
                case "WPos": status.WPos = Vec(val); break;
                case "WCO": status.Wco = Vec(val); break;
                case "Pn": status.Pins = val; break;
                case "Ov": status.Overrides = Vec(val); break;   // feed,rapid,spindle in % (report.c)
                default: break;
            }
        }
        return status;
    }

    private static double[]? Vec(string s)
    {
        var parts = s.Split(',');
        var v = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
        return v;
    }

    /// <summary>
    /// Settings may only be written in Idle or Alarm (system.c: otherwise STATUS_IDLE_ERROR, error 8).
    /// </summary>
    public static bool StateAllowsSettingsWrite(string? state) => state is "Idle" or "Alarm";

    /// <summary>
    /// The exact line to write one setting, or an error text. GRBL rejects negative values (settings.c);
    /// booleans and masks are integers.
    /// </summary>
    public static string? FormatSettingWrite(int number, double value, out string line)
    {
        line = "";
        if (!GrblSettings.Catalog.TryGetValue(number, out var info))
            return $"${number} är inte en känd GRBL 1.1-inställning.";
        if (!double.IsFinite(value) || value < 0)
            return $"${number}: värdet måste vara ett tal ≥ 0 (GRBL avvisar negativa värden).";
        if (info.Kind == GrblSettingKind.Boolean && value is not (0 or 1))
            return $"${number} ({info.Name}) kan bara vara 0 eller 1.";
        if (info.Kind == GrblSettingKind.Mask && (value != Math.Floor(value) || value > 255))
            return $"${number} ({info.Name}) måste vara ett heltal 0–255.";
        if (info.Kind == GrblSettingKind.Integer && value != Math.Floor(value))
            return $"${number} ({info.Name}) måste vara ett heltal.";
        string text = info.Kind == GrblSettingKind.Number
            ? value.ToString("0.###", CultureInfo.InvariantCulture)
            : ((long)value).ToString(CultureInfo.InvariantCulture);
        line = $"${number}={text}";
        return null;
    }

    /// <summary>A console line must fit GRBL's line buffer and contain no control characters.</summary>
    public static string? CheckCommandLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "Tom rad.";
        if (line.Length > MaxLineLength) return $"Raden är för lång ({line.Length} tecken, högst {MaxLineLength}).";
        if (line.Any(ch => ch < 0x20 || ch > 0x7E))
            return "Raden innehåller otillåtna tecken. Realtidskommandon (?, !, ~, Ctrl-X) skickas med knapparna.";
        if (line.Trim() is "?" or "!" or "~")
            return "Realtidskommandon skickas med knapparna, inte som rad.";
        return null;
    }
}

public enum GrblLineKind { Empty, Ok, Error, Alarm, Status, Welcome, Setting, Message, Bracket, Other }

public sealed record GrblLine(GrblLineKind Kind, string Raw, int? Code = null)
{
    /// <summary>A line that ends one send-response exchange.</summary>
    public bool IsCompletion => Kind is GrblLineKind.Ok or GrblLineKind.Error;
}

public sealed class GrblStatus
{
    public string State { get; set; } = "";
    public string? SubState { get; set; }
    public double[]? MPos { get; set; }
    public double[]? WPos { get; set; }
    public double[]? Wco { get; set; }
    public string? Pins { get; set; }
    /// <summary>Feed, rapid and spindle override in percent; only sent now and then (Ov:).</summary>
    public double[]? Overrides { get; set; }
}
