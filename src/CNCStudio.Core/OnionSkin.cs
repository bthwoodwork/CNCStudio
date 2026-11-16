using System;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// Tunt sista lager (onion skin): instead of tabs, a through cut stops <see cref="CamOperation.OnionSkinMm"/> above
/// the bottom, so the parts stay in a thin skin and do not move or fly off; they are cut free with a knife or sanded
/// off afterwards. Only the last pass changes: it goes to the total depth less the skin.
/// </summary>
public static class OnionSkin
{
    public const double MinMm = 0.05, MaxMm = 2;

    public static bool AppliesTo(CamOperation op, double materialThickness) =>
        op.OnionSkinMm > 0 && op.Kind is CamOperationKind.Outside or CamOperationKind.Inside &&
        op.TotalDepth >= materialThickness - 1e-6;

    public static ContourPreview Apply(ContourPreview p, CamOperation op, double materialThickness)
    {
        if (!AppliesTo(op, materialThickness) || p.PassDepths.Count == 0) return p;
        double last = Math.Round(op.TotalDepth - op.OnionSkinMm, 6);
        var depths = p.PassDepths.Where(d => d < last - 1e-6).Append(last).ToList();
        return p with { PassDepths = depths };
    }

    public static string? Validate(CamOperation op)
    {
        if (op.OnionSkinMm == 0) return null;
        if (!double.IsFinite(op.OnionSkinMm) || op.OnionSkinMm < MinMm || op.OnionSkinMm > MaxMm)
            return $"Det sista lagret ska vara {MinMm:0.##}–{MaxMm:0.#} mm (0 = av).";
        if (op.OnionSkinMm >= op.TotalDepth) return "Det sista lagret kan inte vara tjockare än skärdjupet.";
        return null;
    }
}
