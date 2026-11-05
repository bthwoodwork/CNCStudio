namespace CNCStudio.Core;

// Initial integrated V-carve inlay job definition. Configuration only:
// no machining engine or G-code exporter is activated at this stage.
public enum InlayJobKind { FemaleAndMale, FemaleOnly, MaleOnly, VCarveOnly }

public sealed class InlaySettings
{
    public InlayJobKind JobKind { get; set; } = InlayJobKind.FemaleAndMale;
    public double VBitAngleDegrees { get; set; } = 60;
    public double VBitDiameterMm { get; set; } = 12.7;
    public double VBitFlatTipDiameterMm { get; set; } = 0.2;
    public double FemaleDepthMm { get; set; } = 3.0;
    public double MaleDepthMm { get; set; } = 3.0;
    public double MaleStartDepthMm { get; set; } = 1.5;
    public double GlueGapMm { get; set; } = 0.2;
    public double FitClearanceMm { get; set; } = 0.1;
    public double CleanupToolDiameterMm { get; set; } = 3.0;
    public double CleanupStepOverPercent { get; set; } = 40;
    public double FeedRateMmPerMin { get; set; } = 1500;
    public double PlungeRateMmPerMin { get; set; } = 500;
    public double SafeZMm { get; set; } = 5;
    public int SpindleRpm { get; set; } = 18000;
    public bool MirrorFemaleX { get; set; } = true;
    public bool MirrorMaleX { get; set; } = true;
}

public static class InlayValidation
{
    public static string? GetError(InlaySettings v, double materialThicknessMm)
    {
        if (v is null) return "Inlay-inställningarna saknas.";
        if (!Enum.IsDefined(v.JobKind)) return "Okänd jobbtyp.";
        if (!double.IsFinite(materialThicknessMm) || materialThicknessMm <= 0) return "Ogiltig materialtjocklek.";
        if (!Valid(v.VBitAngleDegrees, 1, 179)) return "V-fräsens vinkel måste vara mellan 1 och 179 grader.";
        if (!Valid(v.VBitDiameterMm, 0.01, 100)) return "Ogiltig V-fräsdiameter.";
        if (!Valid(v.VBitFlatTipDiameterMm, 0, v.VBitDiameterMm)) return "Plana spetsen måste vara mindre än fräsdiametern.";
        if (!Valid(v.FemaleDepthMm, 0.01, materialThicknessMm)) return "Honans fräsdjup överstiger materialtjockleken eller är ogiltigt.";
        if (!Valid(v.MaleDepthMm, 0.01, materialThicknessMm)) return "Hanens fräsdjup överstiger materialtjockleken eller är ogiltigt.";
        if (!Valid(v.MaleStartDepthMm, 0, v.MaleDepthMm)) return "Hanens startdjup måste vara mellan 0 och fräsdjupet.";
        if (!Valid(v.GlueGapMm, 0, 20) || v.MaleDepthMm + v.GlueGapMm > materialThicknessMm) return "Hanens djup inklusive limspalt överstiger materialtjockleken.";
        if (!Valid(v.FitClearanceMm, 0, 5)) return "Passningsspel måste vara mellan 0 och 5 mm.";
        if (!Valid(v.CleanupToolDiameterMm, 0.01, 100)) return "Ogiltig planfräsdiameter.";
        if (!Valid(v.CleanupStepOverPercent, 1, 100)) return "Överlapp måste vara mellan 1 och 100 procent.";
        if (!Valid(v.FeedRateMmPerMin, 1, 20000) || !Valid(v.PlungeRateMmPerMin, 1, 10000)) return "Ogiltig matning eller instickshastighet.";
        if (!Valid(v.SafeZMm, 0.1, 100)) return "Säker Z måste vara större än noll.";
        if (v.SpindleRpm < 1 || v.SpindleRpm > 100000) return "Ogiltigt spindelvarvtal.";
        return null;
    }
    private static bool Valid(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max;
}
