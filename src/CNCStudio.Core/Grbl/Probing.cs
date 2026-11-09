using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CNCStudio.Core.Grbl;

/// <summary>What to probe. Z only is the most common and the default.</summary>
public enum ProbeKind { Z, XYZ, XY, X, Y }

/// <summary>Which corner of the stock X/Y probing finds (the plate lies on that corner). +X is right, +Y is away from you.</summary>
public enum ProbeCorner { FrontLeft, FrontRight, BackLeft, BackRight }

/// <summary>
/// The touch plate and how to probe with it. Every value is a setting (Inställningar\probplatta.json,
/// shared by all computers using the same CNC Studio folder).
/// </summary>
public sealed class ProbeSettings
{
    /// <summary>Plate thickness: the plate top is this far above the stock top.</summary>
    public double PlateThicknessMm { get; set; } = 15;
    /// <summary>Corner plate: distance from the plate's outer left side to the stock's left edge.</summary>
    public double PlateWallXMm { get; set; } = 10;
    /// <summary>Corner plate: distance from the plate's outer front side to the stock's front edge.</summary>
    public double PlateWallYMm { get; set; } = 10;
    /// <summary>First, fast search (mm/min).</summary>
    public double FastFeed { get; set; } = 150;
    /// <summary>Second, slow touch for the measured value (mm/min).</summary>
    public double SlowFeed { get; set; } = 40;
    /// <summary>How far to search downwards before giving up (G38.2 then gives ALARM:5).</summary>
    public double MaxTravelZMm { get; set; } = 30;
    /// <summary>How far to search sideways for a wall.</summary>
    public double MaxTravelXYMm { get; set; } = 25;
    /// <summary>Back-off between the fast and the slow touch, and after probing.</summary>
    public double RetractMm { get; set; } = 3;
    /// <summary>X/Y probing: how far to move out sideways from the start before going down beside the plate.</summary>
    public double SideMoveOutMm { get; set; } = 20;
    /// <summary>X/Y probing: how far below the plate top the tool touches the wall.</summary>
    public double SideDepthMm { get; set; } = 5;
    /// <summary>Height above the stock to lift to when done (Z known).</summary>
    public double FinalLiftMm { get; set; } = 10;
    /// <summary>What was probed last time (the dialog opens with it).</summary>
    public ProbeKind LastKind { get; set; } = ProbeKind.Z;
    /// <summary>The corner used last time for X/Y probing.</summary>
    public ProbeCorner Corner { get; set; } = ProbeCorner.FrontLeft;
}

public static class ProbeValidation
{
    public static string? GetError(ProbeSettings s, ProbeKind kind, double toolDiameter)
    {
        bool Pos(double v) => double.IsFinite(v) && v > 0;
        if (!Pos(s.FastFeed) || !Pos(s.SlowFeed) || s.FastFeed > 2000 || s.SlowFeed > s.FastFeed)
            return "Probhastigheterna ska vara större än 0, högst 2000 mm/min, och den långsamma får inte vara snabbare än den snabba.";
        if (!Pos(s.MaxTravelZMm) || !Pos(s.MaxTravelXYMm)) return "Sökavstånden måste vara större än 0.";
        if (!Pos(s.RetractMm)) return "Backavståndet måste vara större än 0.";
        if (!double.IsFinite(s.FinalLiftMm) || s.FinalLiftMm < 0) return "Lyfthöjden efteråt får inte vara negativ.";
        if (!double.IsFinite(s.PlateThicknessMm) || s.PlateThicknessMm < 0)
            return "Plattans tjocklek får inte vara negativ.";
        if (kind != ProbeKind.Z)
        {
            if (!Pos(toolDiameter)) return "Ange fräsens diameter – den behövs för att räkna ut kanten.";
            if (!double.IsFinite(s.PlateWallXMm) || !double.IsFinite(s.PlateWallYMm) || s.PlateWallXMm < 0 || s.PlateWallYMm < 0)
                return "Plattans väggtjocklek får inte vara negativ.";
            if (!Pos(s.SideMoveOutMm)) return "Avståndet ut åt sidan måste vara större än 0.";
            if (!Pos(s.SideDepthMm) || s.SideDepthMm >= s.PlateThicknessMm)
                return "Djupet för sidoprobningen måste vara större än 0 och mindre än plattans tjocklek.";
        }
        return null;
    }
}

/// <summary>Result of a probe cycle. StockTopMPosZ is set when Z was probed (MPos Z of work Z0).</summary>
public sealed record ProbeResult(bool Success, string Message, double? StockTopMPosZ, IReadOnlyList<string> Steps);

/// <summary>
/// Probing with a touch plate, verified against GRBL 1.1 (gcode.c, motion_control.c, report.c):
/// <list type="bullet">
/// <item>G38.2 = probe toward the surface; no contact within the travel → ALARM:5, already in contact → ALARM:4.
/// F and an axis word are required; G91 works.</item>
/// <item>The contact point is reported as [PRB:x,y,z:1] in MACHINE coordinates; it is always in the $# answer.</item>
/// <item>G10 L20 P0 sets the current position in the active coordinate system to the given value.</item>
/// </list>
/// After the slow touch the tool moves to an exact machine position relative to the contact point
/// (G53 G0), so any coasting after the trigger does not matter, and the work coordinate is set there.
/// <para>
/// Z: the tool starts above the plate, which lies on the stock. Corner probing (front-left corner, as the
/// default work zero): the tool starts above the plate near its corner; it probes Z on the plate top, then
/// goes out to the left/front, down beside the plate and touches its outer sides. The stock edge is the
/// plate wall plus the tool radius further in.
/// </para>
/// </summary>
public sealed class ProbeCycle
{
    private readonly GrblClient _g;
    private readonly ProbeSettings _s;
    private readonly List<string> _steps = new();
    private IProgress<string>? _progress;
    private CancellationToken _ct;

    public ProbeCycle(GrblClient grbl, ProbeSettings settings) { _g = grbl; _s = settings; }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(3);

    public async Task<ProbeResult> RunAsync(ProbeKind kind, double toolDiameter, IProgress<string>? progress = null, CancellationToken ct = default,
        ProbeCorner corner = ProbeCorner.FrontLeft)
    {
        _progress = progress; _ct = ct; _steps.Clear();
        if (ProbeValidation.GetError(_s, kind, toolDiameter) is string bad) return new(false, bad, null, _steps);
        double r = toolDiameter / 2;
        double? stockTop = null;
        try
        {
            var status = await _g.RequestStatusAsync(TimeSpan.FromSeconds(2), ct);
            if (status?.State != "Idle") return new(false, $"Maskinen måste stå still (Idle). Nu: {status?.State ?? "okänt"}.", null, _steps);
            if (status.Pins?.Contains('P') == true)
                return new(false, "Proben är redan i kontakt. Kontrollera att klämman inte rör plattan och att kabeln inte är kortsluten.", null, _steps);
            await Send("G21");                                // mm for every value below
            var start = await MachinePosition();

            if (kind is ProbeKind.Z or ProbeKind.XYZ)
            {
                Step("Söker plattans ovansida (Z) …");
                double prbZ = await Touch(2, -1, _s.MaxTravelZMm);
                // Exactly RetractMm above the contact point, then that position is plate + retract in work Z.
                await Send($"G53 G0 Z{N(prbZ + _s.RetractMm)}");
                await Send($"G10 L20 P0 Z{N(_s.PlateThicknessMm + _s.RetractMm)}");
                stockTop = prbZ - _s.PlateThicknessMm;
                Step($"Z0 satt på materialytan (plattan {N(_s.PlateThicknessMm)} mm).");
            }
            if (kind is ProbeKind.XYZ or ProbeKind.XY or ProbeKind.X)
                await ProbeSide(0, Side(corner, 0), start, r, _s.PlateWallXMm);
            if (kind is ProbeKind.XYZ or ProbeKind.XY or ProbeKind.Y)
                await ProbeSide(1, Side(corner, 1), start, r, _s.PlateWallYMm);

            await Send($"G90 G0 Z{N(_s.PlateThicknessMm + _s.FinalLiftMm)}");       // clear of the plate
            await Sync();
            string what = kind switch
            {
                ProbeKind.Z => "Z", ProbeKind.XYZ => "X, Y och Z", ProbeKind.XY => "X och Y", _ => kind.ToString()
            };
            return new(true, $"Probning klar: {what} satt.", stockTop, _steps);
        }
        catch (ProbeFailed ex) { return new(false, ex.Message, null, _steps); }
        catch (TimeoutException ex) { return new(false, "Inget svar från maskinen: " + ex.Message, null, _steps); }
        catch (InvalidOperationException ex) { return new(false, ex.Message, null, _steps); }
    }

    /// <summary>+1 = the wall is approached moving towards plus (left side, front side), −1 = towards minus.</summary>
    public static int Side(ProbeCorner corner, int axis) => axis == 0
        ? (corner is ProbeCorner.FrontLeft or ProbeCorner.BackLeft ? +1 : -1)
        : (corner is ProbeCorner.FrontLeft or ProbeCorner.FrontRight ? +1 : -1);

    /// <summary>
    /// One wall: from the start (above the plate), lift if needed, go out sideways, down beside the plate,
    /// touch the outer side moving inwards (direction <paramref name="s"/>), set the work coordinate, back off and return.
    /// The stock edge – work 0 – is the wall plus the tool radius beyond the contact point.
    /// </summary>
    /// <para>Work Z must be set (Z0 on the stock): XYZ sets it first; for X, Y and XY it must be zeroed before.</para>
    private async Task ProbeSide(int axis, int s, double[] start, double r, double wall)
    {
        string a = axis == 0 ? "X" : "Y";
        string side = axis == 0 ? (s > 0 ? "vänstra" : "högra") : (s > 0 ? "främre" : "bakre");
        Step($"Söker plattans {side} sida ({a}) …");
        await Send($"G90 G0 Z{N(_s.PlateThicknessMm + _s.RetractMm)}");                       // above the plate
        await Send($"G53 G0 {a}{N(start[axis] - s * _s.SideMoveOutMm)}");                     // out beside it
        await Send($"G90 G0 Z{N(_s.PlateThicknessMm - _s.SideDepthMm)}");                     // down beside the wall
        double contact = await Touch(axis, s, _s.SideMoveOutMm + _s.MaxTravelXYMm);
        await Send($"G53 G0 {a}{N(contact - s * _s.RetractMm)}");
        await Send($"G10 L20 P0 {a}{N(-s * (wall + r + _s.RetractMm))}");
        Step($"{a}0 satt på materialets kant (vägg {N(wall)} mm + fräsradie {N(r)} mm).");
        await Send($"G90 G0 Z{N(_s.PlateThicknessMm + _s.RetractMm)}");
        await Send($"G53 G0 {a}{N(start[axis])}");                                           // back over the plate
    }

    /// <summary>Fast search, back off, slow touch (G91 G38.2). Returns the contact coordinate (machine).</summary>
    private async Task<double> Touch(int axis, int dir, double travel)
    {
        string a = "XYZ"[axis].ToString();
        await Send("G91");
        try
        {
            await Send($"G38.2 {a}{N(dir * travel)} F{N(_s.FastFeed)}", ProbeTimeout);
            await Send($"G0 {a}{N(-dir * _s.RetractMm)}");
            await Send($"G38.2 {a}{N(dir * (_s.RetractMm * 2))} F{N(_s.SlowFeed)}", ProbeTimeout);
        }
        finally { await SendQuiet("G90"); }
        var prm = await _g.ReadParametersAsync(_ct);
        if (!prm.TryGetValue("PRB", out var prb) || prb.Length < 3) throw new ProbeFailed("Maskinen rapporterade ingen probpunkt ($# saknar PRB).");
        Step($"Kontakt: maskin-{a} {N(prb[axis])}.");
        return prb[axis];
    }

    private async Task<double[]> MachinePosition()
    {
        await Sync();
        var st = await _g.RequestStatusAsync(TimeSpan.FromSeconds(2), _ct) ?? throw new ProbeFailed("Ingen statusrapport från maskinen.");
        if (st.MPos is { Length: >= 3 } m) return m;
        if (st.WPos is { Length: >= 3 } w)
        {
            var wco = st.Wco ?? (await _g.ReadParametersAsync(_ct)).GetValueOrDefault("G54");
            if (wco is { Length: >= 3 }) return w.Select((v, i) => v + wco[i]).ToArray();
        }
        throw new ProbeFailed("Maskinens position kunde inte läsas.");
    }

    /// <summary>G4 waits until every move before it is done (GRBL syncs the buffer for a dwell).</summary>
    private Task Sync() => Send("G4 P0.01");

    private async Task Send(string line, TimeSpan? timeout = null)
    {
        var r = await _g.SendLineAsync(line, _ct, timeout);
        if (!r.IsOk)
        {
            var text = r.Describe();
            if (text.Contains("ALARM:5") || r.Lines.Any(l => l.Raw.Contains("ALARM:5")))
                throw new ProbeFailed("Ingen kontakt inom sökavståndet (ALARM:5). Kontrollera att klämman sitter på fräsen och att plattan ligger under. Lås upp med $X.");
            if (text.Contains("ALARM:4") || r.Lines.Any(l => l.Raw.Contains("ALARM:4")))
                throw new ProbeFailed("Proben var redan i kontakt när probningen startade (ALARM:4). Lås upp med $X.");
            throw new ProbeFailed($"\"{line}\" misslyckades: {text}");
        }
    }

    private async Task SendQuiet(string line) { try { await _g.SendLineAsync(line, _ct); } catch (Exception) { } }

    private void Step(string text) { _steps.Add(text); _progress?.Report(text); }

    private static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class ProbeFailed : Exception { public ProbeFailed(string m) : base(m) { } }
}
