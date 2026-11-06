using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNCStudio.Core;

public enum MoveKind
{
    /// <summary>G0 rapid. Never below the plunge clearance while moving in XY.</summary>
    Rapid,
    /// <summary>G1 feed move: plunge, cut, or lift/drop at a tab.</summary>
    Feed
}

/// <summary>
/// One planned move in stock-local XY (as the design view) and Z relative to the stock top
/// (0 = surface, negative = into the material). A cutting move follows path
/// <see cref="PathIndex"/> of operation <see cref="OperationIndex"/> from arc length S0 to S1;
/// other moves are straight lines.
/// </summary>
public sealed record MotionMove(MoveKind Kind, double X0, double Y0, double Z0, double X1, double Y1, double Z1,
    string Purpose, int OperationIndex = -1, int PathIndex = -1, double S0 = 0, double S1 = 0, string? FixtureName = null)
{
    public bool FollowsPath => PathIndex >= 0;
    public bool MovesXY => FollowsPath || Math.Abs(X1 - X0) > 1e-9 || Math.Abs(Y1 - Y0) > 1e-9;
    public double XYLength => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
}

public sealed record MotionPlan(IReadOnlyList<MotionMove> Moves, double StartHeight, double MaxZ,
    IReadOnlyList<int> OperationOrder)
{
    public static readonly MotionPlan Empty = new(Array.Empty<MotionMove>(), 0, 0, Array.Empty<int>());
    public double RapidXYLength => Moves.Where(m => m.Kind == MoveKind.Rapid).Sum(m => m.XYLength);
}

/// <summary>
/// Plans the Z motion around the computed contour paths: safe travel, plunges, pass steps,
/// lifts over tabs and retracts – and checks it against fixtures and the Z travel limit.
/// <para>Order: inside contours before outside contours; within an operation, inner (smaller)
/// paths before outer ones, so a part stays held by the surrounding material as long as possible.</para>
/// <para>Not covered: tool holder/collet geometry, ramping, feed rates, lower Z limit.</para>
/// </summary>
public static class MotionPlanner
{
    public static (MotionPlan Plan, IReadOnlyList<CamFinding> Findings) Plan(CncDocument doc,
        IReadOnlyList<CamOperationResult> operations)
    {
        var findings = new List<CamFinding>();
        var m = doc.Motion ?? new MotionSettings();
        if (MotionValidation.GetError(m) is string settingsError)
        {
            findings.Add(new CamFinding(CamSeverity.Critical, "InvalidMotionSettings",
                settingsError + " Förflyttningarna kan inte planeras.", null));
            return (MotionPlan.Empty, findings);
        }
        var analyzed = operations.Select((r, i) => (Result: r, Index: i)).Where(x => x.Result.Analyzed).ToList();
        if (analyzed.Count == 0) return (MotionPlan.Empty, findings);
        if (analyzed.Count < operations.Count)
            findings.Add(new CamFinding(CamSeverity.Warning, "MotionIncomplete",
                $"Förflyttningar är planerade för {analyzed.Count} av {operations.Count} operationer; " +
                "resten saknar beräknad körbana och ingår inte.", null));

        double toolRadius = analyzed.Max(x => x.Result.Operation.ToolDiameter) / 2;
        // Fixtures in stock-local coordinates: the plan's XY is stock-local.
        var fixtures = doc.Fixtures.Select(f => (Fixture: f,
            Left: f.X - doc.Stock.X, Top: f.Y - doc.Stock.Y,
            Right: f.X - doc.Stock.X + f.Width, Bottom: f.Y - doc.Stock.Y + f.Length,
            SafeZ: f.HeightAboveStockMm + f.ClearanceMm)).ToList();
        // The machine's position before the program is unknown: lift above every fixture first.
        double startHeight = Math.Max(m.TravelHeightMm, fixtures.Count == 0 ? 0 : fixtures.Max(f => f.SafeZ));

        var moves = new List<MotionMove>();
        // Pockets and inside contours never free the part, so they go before outside contours.
        var order = analyzed
            .OrderBy(x => x.Result.Operation.Kind == CamOperationKind.Outside ? 1 : 0)
            .ThenBy(x => x.Index).ToList();
        CamPoint? at = null; double z = startHeight;

        // Travel to a start point at a height that clears every fixture on the way, then rapid
        // down to just above the surface.
        void TravelTo(CamPoint start)
        {
            if (at is null)
            {
                moves.Add(new MotionMove(MoveKind.Rapid, start.X, start.Y, startHeight, start.X, start.Y, startHeight,
                    $"Programstart: lyft till Z {F(startHeight)} före första XY-förflyttning"));
                z = startHeight;
            }
            else
            {
                var (travelZ, over) = TravelHeight(at, start, toolRadius, m.TravelHeightMm, fixtures);
                if (z < travelZ - 1e-9)
                {
                    moves.Add(new MotionMove(MoveKind.Rapid, at.X, at.Y, z, at.X, at.Y, travelZ,
                        over is null ? "Lyft till förflyttningshöjd" : $"Lyft över fastspänningen '{over}'",
                        FixtureName: over));
                    z = travelZ;
                }
                moves.Add(new MotionMove(MoveKind.Rapid, at.X, at.Y, z, start.X, start.Y, z,
                    over is null ? "Förflyttning" : $"Förflyttning över fastspänningen '{over}'", FixtureName: over));
            }
            moves.Add(new MotionMove(MoveKind.Rapid, start.X, start.Y, z, start.X, start.Y, m.PlungeClearanceMm,
                "Snabbt ned till höjd före nedmatning"));
            z = m.PlungeClearanceMm;
        }

        // Plunge at the start of a closed loop and cut the whole loop at cutZ. With ramping the tool
        // descends along the path itself (only through material not yet cut: from clearedZ), cuts the
        // rest of the loop at depth and then the ramp section again at full depth.
        void PlungeAndCutLoop(IReadOnlyList<CamPoint> path, int opIndex, int pi, double length,
            double cutZ, double clearedZ, CamOperation op, string purpose, double depth)
        {
            var s = path[0];
            if (op.Plunge == PlungeMode.Ramp && length > 1e-6)
            {
                double fromZ = Math.Min(z, clearedZ);
                if (z > fromZ + 1e-9)
                {
                    moves.Add(new MotionMove(MoveKind.Feed, s.X, s.Y, z, s.X, s.Y, fromZ,
                        fromZ >= -1e-9 ? "Ned till materialytan" : "Ned till föregående djup", opIndex));
                    z = fromZ;
                }
                double drop = fromZ - cutZ;
                if (drop > 1e-9)
                {
                    double rampLength = Math.Min(length, drop / Math.Tan(op.RampAngleDeg * Math.PI / 180));
                    var e = PathMath.PointAt(path, rampLength);
                    moves.Add(new MotionMove(MoveKind.Feed, s.X, s.Y, fromZ, e.X, e.Y, cutZ,
                        $"Rampad nedmatning till {F(depth)} mm", opIndex, pi, 0, rampLength));
                    z = cutZ;
                    AddCut(moves, path, opIndex, pi, rampLength, length, cutZ, purpose);
                    AddCut(moves, path, opIndex, pi, 0, rampLength, cutZ, "Efter rampen");
                    return;
                }
            }
            moves.Add(new MotionMove(MoveKind.Feed, s.X, s.Y, z, s.X, s.Y, cutZ, $"Nedmatning till {F(depth)} mm", opIndex));
            z = cutZ;
            AddCut(moves, path, opIndex, pi, 0, length, cutZ, purpose);
        }

        foreach (var (result, opIndex) in order)
        {
            var preview = result.Preview!;
            var op = result.Operation;
            var paths = preview.Centerlines.Count > 0 ? preview.Centerlines :
                new IReadOnlyList<CamPoint>[] { preview.Centerline };
            if (preview.IsPocket)
            {
                PlanPocket(preview, paths, opIndex, op);
                continue;
            }
            var pathOrder = Enumerable.Range(0, paths.Count).OrderBy(i => BoxArea(paths[i])).ToList();
            foreach (int pi in pathOrder)
            {
                var path = paths[pi];
                if (path.Count < 2) continue;
                var start = path[0];
                double length = PathMath.Length(path);
                TravelTo(start);

                var tabs = result.TabSpans.Where(t => t.PathIndex == pi).OrderBy(t => t.Start).ToList();
                double cleared = 0;
                foreach (var depth in preview.PassDepths)
                {
                    double cutZ = -depth;
                    // Over tabs the tool lifts and drops along the path; ramping there could run into a
                    // tab, so passes that reach the tabs use a straight plunge.
                    if (!tabs.Any(t => cutZ < -t.TopDepth - 1e-9))
                    {
                        PlungeAndCutLoop(path, opIndex, pi, length, cutZ, cleared, op, "Skär längs konturen", depth);
                        cleared = cutZ;
                        continue;
                    }
                    moves.Add(new MotionMove(MoveKind.Feed, start.X, start.Y, z, start.X, start.Y, cutZ,
                        $"Nedmatning till {F(depth)} mm", opIndex));
                    z = cutZ;
                    cleared = cutZ;
                    double s = 0;
                    foreach (var tab in tabs)
                    {
                        double tabZ = -tab.TopDepth;
                        if (cutZ >= tabZ - 1e-9) continue; // this pass is above the tab
                        AddCut(moves, path, opIndex, pi, s, tab.Start, cutZ);
                        var p0 = PathMath.PointAt(path, tab.Start); var p1 = PathMath.PointAt(path, tab.End);
                        moves.Add(new MotionMove(MoveKind.Feed, p0.X, p0.Y, cutZ, p0.X, p0.Y, tabZ, "Lyft över hållflik", opIndex));
                        AddCut(moves, path, opIndex, pi, tab.Start, tab.End, tabZ, "Över hållflik");
                        moves.Add(new MotionMove(MoveKind.Feed, p1.X, p1.Y, tabZ, p1.X, p1.Y, cutZ, "Ned efter hållflik", opIndex));
                        s = tab.End;
                    }
                    AddCut(moves, path, opIndex, pi, s, length, cutZ);
                }
                // 3. Out of the slot (vertical) back to travel height.
                moves.Add(new MotionMove(MoveKind.Rapid, start.X, start.Y, z, start.X, start.Y, m.TravelHeightMm,
                    "Lyft ur spåret"));
                z = m.TravelHeightMm; at = start;
            }
        }

        // Pocket: level by level (the whole area at one depth before going deeper), loops inside out.
        // Between loops the tool feeds across at depth only if the straight link stays inside the
        // pocket's centre region; otherwise it lifts above the surface and plunges again.
        void PlanPocket(ContourPreview preview, IReadOnlyList<IReadOnlyList<CamPoint>> loops, int opIndex, CamOperation op)
        {
            var first = loops[0][0];
            TravelTo(first);
            var pos = first;
            double cleared = 0; // the whole pocket is cleared down to this Z after each level
            foreach (var depth in preview.PassDepths)
            {
                double cutZ = -depth;
                for (int k = 0; k < loops.Count; k++)
                {
                    var loop = loops[k];
                    if (loop.Count < 2) continue;
                    var start = loop[0];
                    bool needPlunge;
                    if (k == 0 && z < m.PlungeClearanceMm - 1e-9)
                    {
                        // Next depth level: back to the innermost loop above the surface.
                        moves.Add(new MotionMove(MoveKind.Rapid, pos.X, pos.Y, z, pos.X, pos.Y, m.PlungeClearanceMm,
                            "Lyft inför nästa djup"));
                        moves.Add(new MotionMove(MoveKind.Rapid, pos.X, pos.Y, m.PlungeClearanceMm, start.X, start.Y,
                            m.PlungeClearanceMm, "Till fickans mitt"));
                        z = m.PlungeClearanceMm;
                        needPlunge = true;
                    }
                    else if (k == 0) needPlunge = true;
                    else if (PocketToolpaths.SegmentInside(pos, start, preview.PocketCenterRegion))
                    {
                        moves.Add(new MotionMove(MoveKind.Feed, pos.X, pos.Y, cutZ, start.X, start.Y, cutZ,
                            "Länk inom fickan", opIndex));
                        needPlunge = false;
                    }
                    else
                    {
                        moves.Add(new MotionMove(MoveKind.Rapid, pos.X, pos.Y, z, pos.X, pos.Y, m.PlungeClearanceMm,
                            "Lyft – länken skulle korsa fickans kant eller en ö"));
                        moves.Add(new MotionMove(MoveKind.Rapid, pos.X, pos.Y, m.PlungeClearanceMm, start.X, start.Y,
                            m.PlungeClearanceMm, "Till nästa varv"));
                        z = m.PlungeClearanceMm;
                        needPlunge = true;
                    }
                    if (needPlunge)
                        PlungeAndCutLoop(loop, opIndex, k, PathMath.Length(loop), cutZ, cleared, op, "Fickfräsning", depth);
                    else
                    {
                        z = cutZ;
                        AddCut(moves, loop, opIndex, k, 0, PathMath.Length(loop), cutZ, "Fickfräsning");
                    }
                    pos = start; // loops are closed
                }
                cleared = cutZ;
            }
            moves.Add(new MotionMove(MoveKind.Rapid, pos.X, pos.Y, z, pos.X, pos.Y, m.TravelHeightMm, "Lyft ur fickan"));
            z = m.TravelHeightMm; at = pos;
        }

        if (at is not null && z < startHeight - 1e-9)
            moves.Add(new MotionMove(MoveKind.Rapid, at.X, at.Y, z, at.X, at.Y, startHeight,
                $"Programslut: lyft till Z {F(startHeight)}"));

        double maxZ = moves.Max(x => Math.Max(x.Z0, x.Z1));
        var plan = new MotionPlan(moves, startHeight, maxZ, order.Select(o => o.Index).ToList());
        findings.AddRange(Check(plan, m, doc.Machine ?? new MachineProfile(), fixtures.Select(f => f.Fixture).ToList()));
        return (plan, findings);
    }

    private static IEnumerable<CamFinding> Check(MotionPlan plan, MotionSettings m, MachineProfile machine,
        IReadOnlyList<FixtureBlock> fixtures)
    {
        double margin = machine.LimitMarginMm;
        if (!machine.HomeOnStartup)
            yield return new CamFinding(CamSeverity.Warning, "NoHoming",
                "Homing vid start ($H) är avstängd i maskininställningarna. Då är maskinkoordinaterna och Z-brytarens läge " +
                "okända, och Z-kontrollerna nedan gäller inte.", null);
        // Defensive: a rapid that moves in XY must never be at or below the surface.
        var bad = plan.Moves.FirstOrDefault(x => x.Kind == MoveKind.Rapid && x.MovesXY &&
            Math.Min(x.Z0, x.Z1) < m.PlungeClearanceMm - 1e-9);
        if (bad is not null)
            yield return new CamFinding(CamSeverity.Critical, "RapidInMaterial",
                $"Snabbförflyttning i sidled på Z {F(Math.Min(bad.Z0, bad.Z1))} – för nära eller i materialet.", null);

        var highest = plan.Moves.OrderByDescending(x => Math.Max(x.Z0, x.Z1)).First();
        string cause = highest.FixtureName is string name ? $" (lyft över '{name}')"
            : fixtures.Count > 0 && plan.StartHeight > m.TravelHeightMm + 1e-9 && Math.Abs(plan.MaxZ - plan.StartHeight) < 1e-9
                ? " (start-/sluthöjd över högsta fastspänningen)" : "";
        if (m.ZMaxAboveStockMm is not double zMax)
            yield return new CamFinding(CamSeverity.Warning, "ZLimitUnknown",
                $"Maskinens Z vid materialytan (MPos Z efter homing och Z-nollning) är inte angiven. Högsta planerade Z är " +
                $"{F(plan.MaxZ)} mm över materialet{cause}, men det går inte att kontrollera att Z-brytaren inte nås. " +
                "Ange värdet under Z-höjder och Z-brytare.", null);
        else
        {
            double stockTop = -zMax; // MPos Z of the stock surface
            double allowed = zMax - margin;
            if (plan.MaxZ > allowed + 1e-9)
                yield return new CamFinding(CamSeverity.Critical, "ZLimitExceeded",
                    $"Planerad höjd {F(plan.MaxZ)} mm över materialet{cause} är över tillåtna {F(allowed)} mm. " +
                    $"Materialytan ligger på MPos Z {F(stockTop)} och Z-brytaren vid MPos Z 0 (efter homing); " +
                    $"marginal {F(margin)} mm. Z-brytaren kan nås.", null);

            // Lower limit: the deepest cut must stay within Z travel ($132) minus the margin.
            double bottom = -machine.ZTravelMm;
            double minZ = plan.Moves.Min(x => Math.Min(x.Z0, x.Z1));
            double deepestMachineZ = stockTop + minZ;
            if (stockTop < bottom - 1e-9)
                yield return new CamFinding(CamSeverity.Critical, "StockTopOutsideTravel",
                    $"Materialytan (MPos Z {F(stockTop)}) ligger under Z-axelns nedre gräns (MPos Z {F(bottom)}). " +
                    "Kontrollera MPos Z och Z-rörelsen i maskininställningarna.", null);
            else if (deepestMachineZ < bottom + margin - 1e-9)
                yield return new CamFinding(CamSeverity.Critical, "ZBottomExceeded",
                    $"Djupaste skär ({F(-minZ)} mm under materialytan) blir MPos Z {F(deepestMachineZ)}, men Z-axeln når " +
                    $"bara ned till MPos Z {F(bottom)} (marginal {F(margin)} mm). Fräsen sticker inte ut tillräckligt – " +
                    "spänn fast den längre ned eller höj materialet.", null);
        }

        foreach (var g in plan.Moves.Where(x => x.FixtureName is not null && x.MovesXY).GroupBy(x => x.FixtureName))
            yield return new CamFinding(CamSeverity.Info, "RaisedOverFixture",
                $"{g.Count()} förflyttning(ar) lyfts till Z {F(g.Max(x => x.Z1))} mm för att passera '{g.Key}'.", null);

        int rapids = plan.Moves.Count(x => x.Kind == MoveKind.Rapid && x.MovesXY);
        yield return new CamFinding(CamSeverity.Info, "MotionSummary",
            $"Förflyttningar på Z {F(m.TravelHeightMm)} mm, start/slut på Z {F(plan.StartHeight)} mm, högsta Z {F(plan.MaxZ)} mm. " +
            $"{rapids} snabbförflyttningar, {F(Math.Round(plan.RapidXYLength))} mm.", null);
    }

    /// <summary>
    /// Height for a rapid from a to b: the travel height, raised above any fixture whose
    /// zone (footprint + tool radius + clearance) the move crosses.
    /// </summary>
    private static (double Z, string? Fixture) TravelHeight(CamPoint a, CamPoint b, double toolRadius, double travel,
        IEnumerable<(FixtureBlock Fixture, double Left, double Top, double Right, double Bottom, double SafeZ)> fixtures)
    {
        double z = travel; string? over = null;
        foreach (var f in fixtures)
        {
            double inflate = toolRadius + Math.Max(0, f.Fixture.ClearanceMm);
            if (f.SafeZ > z && CamSafetyReview.SegmentIntersectsRect(a.X, a.Y, b.X, b.Y,
                    f.Left - inflate, f.Top - inflate, f.Right + inflate, f.Bottom + inflate))
            { z = f.SafeZ; over = f.Fixture.Name; }
        }
        return (z, over);
    }

    private static void AddCut(List<MotionMove> moves, IReadOnlyList<CamPoint> path, int op, int pi,
        double s0, double s1, double z, string purpose = "Skär längs konturen")
    {
        if (s1 - s0 <= 1e-9) return;
        var a = PathMath.PointAt(path, s0); var b = PathMath.PointAt(path, s1);
        moves.Add(new MotionMove(MoveKind.Feed, a.X, a.Y, z, b.X, b.Y, z, purpose, op, pi, s0, s1));
    }

    private static double BoxArea(IReadOnlyList<CamPoint> p) =>
        (p.Max(q => q.X) - p.Min(q => q.X)) * (p.Max(q => q.Y) - p.Min(q => q.Y));

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
