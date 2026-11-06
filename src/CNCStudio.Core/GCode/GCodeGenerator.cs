using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CNCStudio.Core.GCode;

public sealed record GCodeResult(bool Success, IReadOnlyList<string> Lines, IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> VerifierIssues)
{
    public string Text => string.Join("\r\n", Lines) + "\r\n";
}

public enum ExportBlockerKind { NoOperations, CriticalFinding, StalePath, MissingPath, StockTopUnknown, HomingOff, NoMotion }

/// <summary>One reason export is refused; ShapeId/FindingCode point at the cause when known.</summary>
public sealed record ExportBlocker(ExportBlockerKind Kind, string Message, Guid? ShapeId = null, string? FindingCode = null);

/// <summary>
/// Writes GRBL 1.1 G-code from a checked CAM report. Every code used is verified against the GRBL
/// source (docs/GRBL-verifierat.md): G0 G1 G4 G17 G21 G54 G90 G94 M3 M5 M30 with X Y Z F S P.
/// <para>
/// Coordinates: work zero G54 X0 Y0 = the material's FRONT LEFT corner, Z0 = its top surface.
/// The design view has Y growing downwards from the back edge, so Y_gcode = materialHeight − y.
/// </para>
/// <para>
/// Export is refused when anything is unsafe or unchecked: critical findings, operations without a
/// current path, unknown Z limit, or homing at startup turned off. The output is then checked by
/// <see cref="GCodeVerifier"/>, which reads it back independently.
/// </para>
/// </summary>
public static class GCodeGenerator
{
    public const string Generator = "CNC Studio 0.8.1";

    public static IReadOnlyList<string> BlockingReasons(CncDocument doc, CamReport report) =>
        Blockers(doc, report).Select(b => b.Message).ToList();

    /// <summary>
    /// Why export is refused, with enough structure (kind, object, finding code) for the UI to
    /// suggest a fix and take the user to the right place. One entry per distinct message.
    /// </summary>
    public static IReadOnlyList<ExportBlocker> Blockers(CncDocument doc, CamReport report)
    {
        var list = new List<ExportBlocker>();
        if (report.Operations.Count == 0)
            list.Add(new(ExportBlockerKind.NoOperations, "Det finns inga CAM-operationer."));
        foreach (var f in report.AllFindings.Where(f => f.Severity == CamSeverity.Critical)
                     .GroupBy(f => (f.Code, f.ShapeId)).Select(g => g.First()))
            list.Add(new(ExportBlockerKind.CriticalFinding, "Kritiskt: " + f.Message, f.ShapeId, f.Code));
        foreach (var o in report.Operations.Where(o => !o.Analyzed))
            list.Add(o.Stale
                ? new(ExportBlockerKind.StalePath, "En körbana är inaktuell – klicka Beräkna körbanor.", o.Operation.ShapeId)
                : new(ExportBlockerKind.MissingPath, "En operation saknar beräknad körbana.", o.Operation.ShapeId));
        if (doc.Motion.ZMaxAboveStockMm is null)
            list.Add(new(ExportBlockerKind.StockTopUnknown,
                "Maskinens Z vid materialytan (MPos Z) är inte angiven – Z-brytaren kan inte kontrolleras."));
        if (!doc.Machine.HomeOnStartup)
            list.Add(new(ExportBlockerKind.HomingOff, "Homing vid start ($H) är avstängd – maskinkoordinaterna är okända."));
        if (report.Motion.Moves.Count == 0 && report.Operations.Count > 0)
            list.Add(new(ExportBlockerKind.NoMotion, "Ingen rörelseplan kunde skapas."));
        return list.GroupBy(b => b.Message).Select(g => g.First()).ToList();
    }

    public static GCodeResult Generate(CncDocument doc, CamReport report, DateTime? now = null)
    {
        var blocking = BlockingReasons(doc, report);
        var warnings = report.AllFindings.Where(f => f.Severity == CamSeverity.Warning)
            .Select(f => f.Message).Distinct().ToList();
        warnings.AddRange(RateWarnings(doc, report));
        if (blocking.Count > 0)
            return new GCodeResult(false, Array.Empty<string>(), blocking, warnings, Array.Empty<string>());

        var plan = report.Motion;
        string XY(double dx, double dy) { var (wx, wy) = doc.ToWork(dx, dy); return $"X{F(wx)} Y{F(wy)}"; }
        var lines = new List<string>();
        void Comment(string text)
        {
            foreach (var chunk in Wrap(Ascii(text), GCodeVerifier.MaxLineLength - 2)) lines.Add($"({chunk})");
        }

        Comment($"{Generator} - {doc.Name}");
        Comment($"Skapad {(now ?? DateTime.Now):yyyy-MM-dd HH:mm}");
        Comment($"Material {F(doc.MaterialWidth)} x {F(doc.MaterialHeight)} x {F(doc.MaterialThickness)} mm");
        Comment(doc.WorkZero == WorkZero.Center
            ? "Nollpunkt G54: X0 Y0 = materialets MITT, Z0 = materialets ovansida"
            : "Nollpunkt G54: X0 Y0 = materialets framre vanstra horn, Z0 = materialets ovansida");
        Comment("Krav: maskinen homad med $H, laserlage av ($32=0), Z-nollan satt pa materialytan");
        bool manual = doc.Machine.Spindle == SpindleControl.Manual;
        Comment(manual
            ? "Fras: MANUELL. Programmet pausar med M0 - starta frasen, stall varvtalet och fortsatt med ~"
            : "Fras: styrs av GRBL med M3/M5");
        foreach (var o in report.Operations.Select(o => o.Operation)
                     .GroupBy(o => (o.ToolName, o.ToolDiameter)).Select(g => g.First()))
            Comment($"Verktyg: {(string.IsNullOrWhiteSpace(o.ToolName) ? "fras" : o.ToolName)}, diameter {F(o.ToolDiameter)} mm");
        foreach (var w in warnings) Comment("VARNING: " + w);

        // Modal setup: mm, absolute, feed per minute, XY plane, work coordinate system G54.
        lines.Add("G21"); lines.Add("G90"); lines.Add("G94"); lines.Add("G17"); lines.Add("G54");

        var ops = report.Operations;
        double? feed = null;
        double? rpm = null;
        double x = double.NaN, y = double.NaN, z = double.NaN;

        void EnsureSpindle(int opIndex)
        {
            if (opIndex < 0 || opIndex >= ops.Count) return;
            var want = ops[opIndex].Operation.SpindleRpm;
            if (rpm is double r && Math.Abs(r - want) < 0.5) return;
            if (manual)
            {
                // M0 = program pause, resumed with '~' (cycle start) – verified in the GRBL wiki.
                Comment(rpm is null
                    ? $"PAUS: starta frasen for hand, stall {F(want, 0)} varv/min, fortsatt med ~"
                    : $"PAUS: stall om frasen till {F(want, 0)} varv/min, fortsatt med ~");
                lines.Add("M0");
            }
            else
            {
                lines.Add($"M3 S{F(want, 0)}");
                if (doc.Motion.SpindleSpinUpSeconds > 0) lines.Add($"G4 P{F(doc.Motion.SpindleSpinUpSeconds, 2)}");
            }
            rpm = want;
        }
        string Feed(double f)
        {
            if (feed is double cur && Math.Abs(cur - f) < 1e-9) return "";
            feed = f;
            return $" F{F(f, 0)}";
        }

        for (int i = 0; i < plan.Moves.Count; i++)
        {
            var m = plan.Moves[i];
            if (i == 0)
            {
                // Position unknown: lift Z alone first, start the spindle at that safe height,
                // then go to the first path start.
                lines.Add($"G0 Z{F(m.Z1)}"); z = m.Z1;
                EnsureSpindle(plan.Moves.FirstOrDefault(n => n.OperationIndex >= 0)?.OperationIndex ?? -1);
                lines.Add($"G0 {XY(m.X1, m.Y1)}"); x = m.X1; y = m.Y1;
                continue;
            }
            if (m.Kind == MoveKind.Rapid)
            {
                // A speed change for the next operation happens here, at travel height, before
                // the tool moves over to it – not down near the material.
                if (m.MovesXY)
                    EnsureSpindle(plan.Moves.Skip(i + 1).FirstOrDefault(n => n.OperationIndex >= 0)?.OperationIndex ?? -1);
                if (m.MovesXY) { lines.Add($"G0 {XY(m.X1, m.Y1)}" + (Same(z, m.Z1) ? "" : $" Z{F(m.Z1)}")); x = m.X1; y = m.Y1; z = m.Z1; }
                else if (!Same(z, m.Z1)) { lines.Add($"G0 Z{F(m.Z1)}"); z = m.Z1; }
                continue;
            }
            // Feed moves: the spindle must be running before the first one of each operation.
            EnsureSpindle(m.OperationIndex);
            var op = ops[m.OperationIndex].Operation;
            if (!m.FollowsPath)
            {
                if (m.MovesXY)
                {
                    // Straight link at depth (inside a pocket): cutting feed.
                    lines.Add($"G1 {XY(m.X1, m.Y1)}" + (Same(z, m.Z1) ? "" : $" Z{F(m.Z1)}") + Feed(op.FeedRate));
                    x = m.X1; y = m.Y1; z = m.Z1;
                }
                else if (!Same(z, m.Z1)) { lines.Add($"G1 Z{F(m.Z1)}" + Feed(op.PlungeRate)); z = m.Z1; }
                continue;
            }
            var preview = ops[m.OperationIndex].Preview!;
            var path = preview.Centerlines.Count > 0 ? preview.Centerlines[m.PathIndex] : preview.Centerline;
            var slice = PathMath.Slice(path, m.S0, m.S1);
            bool ramp = !Same(m.Z0, m.Z1);
            double total = ramp ? PathMath.Length(slice) : 0, run = 0;
            for (int k = 1; k < slice.Count; k++)
            {
                var p = slice[k];
                run += PathMath.Distance(slice[k - 1], p);
                if (Same(x, p.X) && Same(y, p.Y)) continue;
                if (ramp)
                {
                    // Ramped plunge: Z falls linearly with distance along the path.
                    double pz = k == slice.Count - 1 || total <= 0 ? m.Z1 : m.Z0 + (m.Z1 - m.Z0) * run / total;
                    lines.Add($"G1 {XY(p.X, p.Y)} Z{F(pz)}" + Feed(op.FeedRate));
                    z = pz;
                }
                else lines.Add($"G1 {XY(p.X, p.Y)}" + Feed(op.FeedRate));
                x = p.X; y = p.Y;
            }
            if (ramp) z = m.Z1;
        }
        if (manual) Comment("Klart - stang av frasen for hand");
        else lines.Add("M5");
        lines.Add("M30");

        var issues = GCodeVerifier.Verify(lines, new GCodeVerifier.Limits(
            ManualSpindle: manual,
            PlungeClearance: doc.Motion.PlungeClearanceMm,
            MaxZ: doc.Motion.ZMaxAboveStockMm!.Value - doc.Machine.LimitMarginMm,
            MinZ: -ops.Where(o => o.Analyzed).Max(o => o.Operation.TotalDepth),
            MinX: -ops.Max(o => o.Operation.ToolDiameter) - doc.Machine.WorkWidthMm,
            MaxX: doc.Machine.WorkWidthMm,
            MinY: -ops.Max(o => o.Operation.ToolDiameter) - doc.Machine.WorkLengthMm,
            MaxY: doc.Machine.WorkLengthMm,
            EndZ: doc.Motion.TravelHeightMm));
        return issues.Count == 0
            ? new GCodeResult(true, lines, Array.Empty<string>(), warnings, issues)
            : new GCodeResult(false, Array.Empty<string>(),
                new[] { "Den genererade G-coden klarade inte kontrollen – ingen fil skapas. Detta är ett programfel." },
                warnings, issues);
    }

    private static IEnumerable<string> RateWarnings(CncDocument doc, CamReport report)
    {
        var m = doc.Machine;
        double? xy = m.MaxRateX is double a && m.MaxRateY is double b ? Math.Min(a, b) : null;
        foreach (var o in report.Operations.Select(o => o.Operation).Distinct())
        {
            if (xy is double limit && o.FeedRate > limit)
                yield return $"Matningen {F(o.FeedRate, 0)} mm/min är högre än maskinens maxhastighet ($110/$111 = {F(limit, 0)}).";
            if (m.MaxRateZ is double zl && o.PlungeRate > zl)
                yield return $"Nedmatningen {F(o.PlungeRate, 0)} mm/min är högre än maskinens Z-maxhastighet ($112 = {F(zl, 0)}).";
        }
    }

    private static bool Same(double a, double b) => !double.IsNaN(a) && Math.Abs(a - b) < 0.0005;

    /// <summary>Up to three decimals, '.' as separator, never "-0".</summary>
    public static string F(double v, int decimals = 3)
    {
        var r = Math.Round(v, decimals, MidpointRounding.AwayFromZero);
        if (r == 0) r = 0; // drop negative zero
        return r.ToString("0." + new string('#', Math.Max(decimals, 0)), CultureInfo.InvariantCulture).TrimEnd('.');
    }

    /// <summary>Comments stay plain ASCII so every sender (including ours) accepts them.</summary>
    public static string Ascii(string s)
    {
        var map = new Dictionary<char, string> { ['å'] = "a", ['ä'] = "a", ['ö'] = "o", ['Å'] = "A", ['Ä'] = "A",
            ['Ö'] = "O", ['é'] = "e", ['×'] = "x", ['–'] = "-", ['—'] = "-", ['−'] = "-", ['ø'] = "o", ['²'] = "2" };
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (map.TryGetValue(c, out var r)) sb.Append(r);
            else if (c >= 0x20 && c <= 0x7E && c != '(' && c != ')') sb.Append(c);
            else if (c == '(' || c == ')') sb.Append(c == '(' ? '[' : ']');
            else sb.Append('?');
        }
        return sb.ToString();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var words = text.Split(' ');
        var line = new StringBuilder();
        foreach (var w in words)
        {
            if (line.Length > 0 && line.Length + 1 + w.Length > width) { yield return line.ToString(); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(w.Length > width ? w[..width] : w);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
