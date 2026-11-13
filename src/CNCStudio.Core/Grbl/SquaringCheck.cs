using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CNCStudio.Core.Grbl;

/// <summary>How square the machine is, in three steps.</summary>
public enum SquaringVerdict
{
    /// <summary>Less than 0.1° off – no adjustment needed.</summary>
    Square,
    /// <summary>A little off: one end of the gantry is at most <see cref="SquaringCheck.SlightOffsetMm"/> out.</summary>
    Slight,
    /// <summary>Noticeably off – adjust the gantry.</summary>
    NeedsAdjustment
}

/// <summary>
/// What was moved and measured. Point 1 is where the tool starts, point 2 after the X move and point 3
/// after the Y move, so 1–2 runs along X, 2–3 along Y and 1–3 is the diagonal.
/// </summary>
/// <param name="MoveX">The X move, with sign (+ = to the right).</param>
/// <param name="MoveY">The Y move, with sign (+ = away from you, backwards).</param>
/// <param name="A">Measured distance 1–2 (mm).</param>
/// <param name="B">Measured distance 2–3 (mm).</param>
/// <param name="C">Measured distance 1–3, the diagonal (mm).</param>
/// <param name="GantrySpan">Distance between the two Y rails – how long the gantry is (mm).</param>
public sealed record SquaringInput(double MoveX, double MoveY, double A, double B, double C, double GantrySpan);

/// <param name="AngleErrorDeg">How far the angle between the axes is from 90°. Positive = the gantry's right end is further back than its left end.</param>
/// <param name="ExpectedDiagonal">The diagonal a square machine would give with the measured sides (√(a² + b²)).</param>
/// <param name="DiagonalError">Measured minus expected diagonal (mm).</param>
/// <param name="GantryOffsetMm">How much further back (+) or forward (−) the right end of the gantry is than it should be, over <see cref="SquaringInput.GantrySpan"/>.</param>
/// <param name="UncertaintyDeg">The angle error a measuring error of <see cref="SquaringCheck.MeasuringErrorMm"/> on every distance could give.</param>
/// <param name="XLengthErrorPercent">Measured 1–2 compared with the X move (+ = went further than commanded).</param>
/// <param name="YLengthErrorPercent">Measured 2–3 compared with the Y move.</param>
public sealed record SquaringResult(SquaringInput Input, double AngleErrorDeg, double ExpectedDiagonal, double DiagonalError,
    double GantryOffsetMm, double UncertaintyDeg, double XLengthErrorPercent, double YLengthErrorPercent, SquaringVerdict Verdict)
{
    /// <summary>The error is smaller than what measuring with a ruler can tell.</summary>
    public bool WithinMeasuringError => Math.Abs(AngleErrorDeg) <= UncertaintyDeg;
    /// <summary>X or Y went noticeably further or shorter than commanded – worth checking steps/mm.</summary>
    public bool StepsWorthChecking => Math.Abs(XLengthErrorPercent) > SquaringCheck.StepsHintPercent || Math.Abs(YLengthErrorPercent) > SquaringCheck.StepsHintPercent;
}

/// <summary>
/// XY squaring (like gSender's tool): the machine moves along X and then along Y, three points are marked
/// and the three distances between them measured. The law of cosines gives the angle between the axes.
/// <para>
/// Unlike gSender, the advice is how far one end of the gantry has to move – the gantry's length × the
/// angle error – and which end and which way, from the sign of the error and the directions moved.
/// (gSender advises moving the rail by the diagonal's error, which with a 300 × 300 triangle is only
/// just over a quarter of what is needed on a 750 mm gantry, and always in the same direction.) Steps/mm are
/// not written from here: a ruler over a few hundred mm is too coarse for that; Kalibrera steg/mm is
/// the tool for it.
/// </para>
/// Directions follow the program's convention (Första test): X+ to the right, Y+ away from you.
/// </summary>
public static class SquaringCheck
{
    /// <summary>Below this the machine counts as square (gSender's limit too).</summary>
    public const double SquareDeg = 0.1;
    /// <summary>Up to this much at the end of the gantry counts as slightly off.</summary>
    public const double SlightOffsetMm = 2;
    /// <summary>The measuring error assumed for each distance when telling how sure the result is.</summary>
    public const double MeasuringErrorMm = 0.5;
    /// <summary>A side this much longer or shorter than the move suggests checking steps/mm.</summary>
    public const double StepsHintPercent = 0.3;

    public static string? Validate(SquaringInput i)
    {
        if (!double.IsFinite(i.MoveX) || Math.Abs(i.MoveX) < 50) return "Flytta minst 50 mm i X – ju större triangel, desto säkrare resultat.";
        if (!double.IsFinite(i.MoveY) || Math.Abs(i.MoveY) < 50) return "Flytta minst 50 mm i Y – ju större triangel, desto säkrare resultat.";
        foreach (var (v, name) in new[] { (i.A, "1–2"), (i.B, "2–3"), (i.C, "1–3") })
            if (!double.IsFinite(v) || v <= 0) return $"Ange sträckan {name} i mm.";
        if (!double.IsFinite(i.GantrySpan) || i.GantrySpan <= 0 || i.GantrySpan > 5000) return "Ange avståndet mellan Y-skenorna i mm (ungefär räcker).";
        if (i.A + i.B <= i.C || i.A + i.C <= i.B || i.B + i.C <= i.A) return "Sträckorna går inte ihop till en triangel – mät igen.";
        // Far from the move = most likely measured between the wrong points or misread.
        if (Math.Abs(i.A - Math.Abs(i.MoveX)) > 0.1 * Math.Abs(i.MoveX)) return $"1–2 ska vara nära X-flytten ({F(Math.Abs(i.MoveX))} mm). Kontrollera mätningen.";
        if (Math.Abs(i.B - Math.Abs(i.MoveY)) > 0.1 * Math.Abs(i.MoveY)) return $"2–3 ska vara nära Y-flytten ({F(Math.Abs(i.MoveY))} mm). Kontrollera mätningen.";
        return null;
    }

    public static SquaringResult Compute(SquaringInput i)
    {
        double phi = AngleErrorRad(i, i.A, i.B, i.C);
        double expected = Math.Sqrt(i.A * i.A + i.B * i.B);
        // How sure: the worst case of a measuring error on each distance (first-order, summed).
        double u = 0, h = 0.01;
        foreach (var k in new[] { 0, 1, 2 })
        {
            double a = i.A + (k == 0 ? h : 0), b = i.B + (k == 1 ? h : 0), c = i.C + (k == 2 ? h : 0);
            u += Math.Abs(AngleErrorRad(i, a, b, c) - phi) / h * MeasuringErrorMm;
        }
        double deg = phi * 180 / Math.PI, offset = i.GantrySpan * Math.Tan(phi);
        var verdict = Math.Abs(deg) < SquareDeg ? SquaringVerdict.Square
            : Math.Abs(offset) <= SlightOffsetMm ? SquaringVerdict.Slight : SquaringVerdict.NeedsAdjustment;
        return new SquaringResult(i, deg, expected, i.C - expected, offset, u * 180 / Math.PI,
            (i.A - Math.Abs(i.MoveX)) / Math.Abs(i.MoveX) * 100, (i.B - Math.Abs(i.MoveY)) / Math.Abs(i.MoveY) * 100, verdict);
    }

    /// <summary>
    /// The angle φ the gantry (X direction) is turned from square to the rails (Y direction), positive
    /// when its right end is further back. With X dir (cos φ, sin φ) and Y dir (0, 1), the angle at point 2
    /// between 2→1 and 2→3 has cos = −sx·sy·sin φ, and the law of cosines gives that cosine from a, b, c.
    /// </summary>
    private static double AngleErrorRad(SquaringInput i, double a, double b, double c)
    {
        double cosAt2 = (a * a + b * b - c * c) / (2 * a * b);
        double sinPhi = -Math.Sign(i.MoveX) * Math.Sign(i.MoveY) * cosAt2;
        return Math.Asin(Math.Clamp(sinPhi, -1, 1));
    }

    /// <summary>Plain-language result: how far off, what to adjust, and how sure it is.</summary>
    public static string Describe(SquaringResult r)
    {
        var lines = new List<string>();
        double off = Math.Abs(r.GantryOffsetMm);
        lines.Add(r.Verdict switch
        {
            SquaringVerdict.Square => $"Maskinen är vinkelrät: {F(r.AngleErrorDeg, "0.00")}° från 90°. Ingen justering behövs.",
            SquaringVerdict.Slight => $"Maskinen är lite sned: {F(r.AngleErrorDeg, "0.00")}° från 90°, {F(off, "0.0")} mm över gantryts längd.",
            _ => $"Maskinen behöver justeras: {F(r.AngleErrorDeg, "0.00")}° från 90°, {F(off, "0.0")} mm över gantryts längd."
        });
        if (r.Verdict != SquaringVerdict.Square)
        {
            string offWhere = r.GantryOffsetMm > 0 ? "för långt bak" : "för långt fram";
            string fixRight = r.GantryOffsetMm > 0 ? "framåt" : "bakåt", fixLeft = r.GantryOffsetMm > 0 ? "bakåt" : "framåt";
            lines.Add($"Gantryts högra ände ligger {F(off, "0.0")} mm {offWhere} jämfört med den vänstra. " +
                      $"Flytta högra änden {F(off, "0.0")} mm {fixRight}, eller vänstra änden {F(off, "0.0")} mm {fixLeft} " +
                      $"(räknat på {F(r.Input.GantrySpan)} mm mellan Y-skenorna). Gör om kontrollen efteråt.");
        }
        lines.Add($"Diagonalen 1–3 är {F(r.Input.C, "0.0")} mm, och en vinkelrät maskin hade gett {F(r.ExpectedDiagonal, "0.0")} mm ({Signed(r.DiagonalError)} mm).");
        lines.Add(r.WithinMeasuringError
            ? $"Obs: felet är mindre än mätosäkerheten (±{F(r.UncertaintyDeg, "0.00")}° om varje mått kan vara {F(MeasuringErrorMm)} mm fel). Gör triangeln större eller mät noggrannare innan du justerar."
            : $"Mätosäkerhet: ±{F(r.UncertaintyDeg, "0.00")}° om varje mått kan vara {F(MeasuringErrorMm)} mm fel.");
        if (r.StepsWorthChecking)
            lines.Add($"X gick {Signed(r.XLengthErrorPercent, "0.00")} % och Y {Signed(r.YLengthErrorPercent, "0.00")} % mot beordrat. " +
                      "Kontrollera steg/mm med Kalibrera steg/mm (en axel i taget med skjutmått) – vinkeln påverkas inte av det.");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Directions for the triangle that keep both moves inside the machine: right and back if possible,
    /// otherwise the other way on the axis that does not fit. <paramref name="check"/> tells whether a move
    /// from the current position fits (null = fits). Returns null signs and the reason when neither fits.
    /// </summary>
    public static (int Sx, int Sy, string? Problem) ChooseDirections(double distX, double distY, Func<double, double, string?> check)
    {
        foreach (var sx in new[] { +1, -1 })
            foreach (var sy in new[] { +1, -1 })
                if (Fits(sx, sy, distX, distY, check) is null) return (sx, sy, null);
        return (0, 0, Fits(+1, +1, distX, distY, check));
    }

    /// <summary>Whether the X move and then the Y move stay inside the machine (null = they do).</summary>
    public static string? Fits(int sx, int sy, double distX, double distY, Func<double, double, string?> check) =>
        check(sx * distX, 0) ?? check(sx * distX, sy * distY);

    private static string F(double v, string f = "0.###") => v.ToString(f, CultureInfo.InvariantCulture);
    private static string Signed(double v, string f = "0.0") => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString(f, CultureInfo.InvariantCulture);
}

/// <summary>One saved squaring check.</summary>
public sealed record SquaringEntry
{
    public DateTime Time { get; init; }
    public string Computer { get; init; } = "";
    public double MoveX { get; init; }
    public double MoveY { get; init; }
    public double A { get; init; }
    public double B { get; init; }
    public double C { get; init; }
    public double GantrySpan { get; init; }
    public double AngleErrorDeg { get; init; }
    public double GantryOffsetMm { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// The squaring checks of one machine (Inställningar\Maskiner\Vinkelkontroll\&lt;id&gt;.json), so a check
/// after an adjustment can be compared with the one before. Kept beside the machine's profile, not in it,
/// so projects do not carry the history.
/// </summary>
public sealed class SquaringLog
{
    public const string FolderName = "Vinkelkontroll";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<SquaringEntry> Entries { get; set; } = new();
    /// <summary>The distance between the Y rails the user gave last time.</summary>
    public double? GantrySpan { get; set; }

    public static string PathFor(string machinesFolder, string machineId) => Path.Combine(machinesFolder, FolderName, machineId + ".json");

    public static SquaringLog Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<SquaringLog>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (Exception) { return new(); }   // a damaged file must not stop the check
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AppStorage.WriteAtomic(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Adds the result unless it is the same measurement as the newest entry. Returns true if added.</summary>
    public bool Add(SquaringResult r, DateTime time, string computer)
    {
        var i = r.Input;
        GantrySpan = i.GantrySpan;
        if (Entries.LastOrDefault() is { } last && last.MoveX == i.MoveX && last.MoveY == i.MoveY && last.A == i.A && last.B == i.B &&
            last.C == i.C && last.GantrySpan == i.GantrySpan) return false;
        Entries.Add(new SquaringEntry
        {
            Time = time, Computer = computer, MoveX = i.MoveX, MoveY = i.MoveY, A = i.A, B = i.B, C = i.C, GantrySpan = i.GantrySpan,
            AngleErrorDeg = r.AngleErrorDeg, GantryOffsetMm = r.GantryOffsetMm
        });
        return true;
    }
}
