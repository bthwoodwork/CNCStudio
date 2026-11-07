using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>Severity levels of the CAM Safety &amp; Validation Engine.</summary>
public enum CamSeverity
{
    /// <summary>Confirmed unsafe or invalid. Must block future G-code export.</summary>
    Critical = 0,
    /// <summary>Needs the user's review.</summary>
    Warning = 1,
    /// <summary>Suggestion or non-critical note.</summary>
    Info = 2
}

/// <summary>
/// One finding. When <see cref="HasSegment"/> is true, TStart/TEnd (0..1) locate the
/// affected part of segment <see cref="SegmentIndex"/> in path <see cref="PathIndex"/>.
/// </summary>
public sealed record CamFinding(CamSeverity Severity, string Code, string Message, Guid? ShapeId,
    Guid? FixtureId = null, int PathIndex = -1, int SegmentIndex = -1, double TStart = 0, double TEnd = 1)
{
    public bool HasSegment => PathIndex >= 0 && SegmentIndex >= 0;
}

/// <summary>Result for one CAM operation. Preview is null when no toolpath could be computed.</summary>
public sealed record CamOperationResult(CamOperation Operation, DesignShape? Shape,
    ContourPreview? Preview, IReadOnlyList<CamFinding> Findings, bool Stale = false,
    IReadOnlyList<TabSpan>? Tabs = null)
{
    public IReadOnlyList<TabSpan> TabSpans => Tabs ?? Array.Empty<TabSpan>();
    /// <summary>True only when a toolpath exists and the available checks have run on it.</summary>
    public bool Analyzed => Preview is not null;
    public CamSeverity? Worst => Findings.Count == 0 ? null : Findings.Min(f => f.Severity);
}

public sealed record CamReport(IReadOnlyList<CamOperationResult> Operations)
{
    public static readonly CamReport Empty = new(Array.Empty<CamOperationResult>());
    /// <summary>Planned Z motion between and around the cuts.</summary>
    public MotionPlan Motion { get; init; } = MotionPlan.Empty;
    /// <summary>Findings that belong to the whole program (motion, Z limit) rather than one object.</summary>
    public IReadOnlyList<CamFinding> GlobalFindings { get; init; } = Array.Empty<CamFinding>();
    public IEnumerable<CamFinding> AllFindings => Operations.SelectMany(o => o.Findings).Concat(GlobalFindings);
    public int CriticalCount => AllFindings.Count(f => f.Severity == CamSeverity.Critical);
    public int WarningCount => AllFindings.Count(f => f.Severity == CamSeverity.Warning);
    public int InfoCount => AllFindings.Count(f => f.Severity == CamSeverity.Info);
    public int NotAnalyzedCount => Operations.Count(o => !o.Analyzed);
    public int StaleCount => Operations.Count(o => o.Stale);
    public CamOperationResult? For(Guid shapeId) => Operations.FirstOrDefault(o => o.Operation.ShapeId == shapeId);
}

/// <summary>
/// Settings-level checks that do not need a computed toolpath.
/// </summary>
public static class CamOperationChecks
{
    /// <summary>A floor thinner than this under a partial-depth cut is flagged.</summary>
    public const double ThinFloorMm = 1.0;

    public static IReadOnlyList<CamFinding> Analyze(CamOperation op, DesignShape shape, double materialThickness)
    {
        var list = new List<CamFinding>();
        var id = shape.Id;
        if (op.ToolKind == ToolKind.VBit)
            list.Add(new CamFinding(CamSeverity.Critical, "VBitNotSupported",
                "En V-fräs kan inte användas för kontur eller ficka – den skär inte med full diameter. " +
                "Välj en rak fräs (V-carving kommer i en senare version).", id));
        if (op.ToolKind == ToolKind.BallNose)
            list.Add(new CamFinding(CamSeverity.Info, "BallNoseFloor",
                "Kulfräs: botten och väggens nederkant blir rundade, inte plana.", id));
        if (op.Kind == CamOperationKind.Drill && !DrillToolpaths.MatchesTool(shape, op))
            list.Add(new CamFinding(CamSeverity.Warning, "HoleSizeMismatch",
                $"Borrningen ger ett hål med fräsens diameter (Ø{F(op.ToolDiameter)} mm) i objektets mitt, men objektet är " +
                $"{F(shape.Width)} × {F(shape.Height)} mm. Välj en fräs med objektets diameter, eller ficka för ett större hål.", id));
        if (op.Kind != CamOperationKind.Drill && op.DepthPerPass > op.ToolDiameter + 1e-9)
            list.Add(new CamFinding(CamSeverity.Warning, "DeepPass",
                $"Djup per pass ({F(op.DepthPerPass)} mm) är större än fräsdiametern ({F(op.ToolDiameter)} mm). " +
                "Risk för fräsbrott; ett vanligt riktvärde i trä är högst 1 × diametern.", id));
        bool through = op.TotalDepth >= materialThickness - 1e-6;
        bool contour = op.Kind is CamOperationKind.Outside or CamOperationKind.Inside;
        double remaining = materialThickness - op.TotalDepth;
        if (!through && remaining < ThinFloorMm)
            list.Add(new CamFinding(CamSeverity.Warning, "ThinFloor",
                $"Bara {F(remaining)} mm material kvar under spåret. Risk att det bryts igenom eller att " +
                "delen lossnar – välj genomskärning eller ett grundare djup.", id));
        if (through)
        {
            list.Add(new CamFinding(CamSeverity.Info, "ThroughCut",
                $"Genomskärning ({F(op.TotalDepth)} mm i {F(materialThickness)} mm material). " +
                "Använd offerskiva och kontrollera Z-nollpunkten.", id));
            if (contour && !op.UseTabs)
                list.Add(new CamFinding(CamSeverity.Warning, "LoosePart",
                    "Genomskärning utan hållflikar: den utskurna delen kan lossna och slungas iväg av fräsen.", id));
            if (contour && op.UseTabs && op.TabHeight >= op.TotalDepth - 1e-9)
                list.Add(new CamFinding(CamSeverity.Warning, "TabTooHigh",
                    $"Flikhöjden ({F(op.TabHeight)} mm) är lika stor som skärdjupet – vid flikarna skärs inget alls.", id));
        }
        // Tabs on a partial-depth cut are simply not applied; no message (tabs are on by default).
        return list;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Computes toolpath previews and all available checks for a document.
/// Previews are cached per operation and recomputed only when the shape geometry,
/// CAM settings or material thickness change. Fixture, stock and machine changes
/// re-run only the (cheap) XY review.
/// <para>
/// Cheap shapes (rectangles, ellipses, small vector paths) are recomputed automatically.
/// Heavy shapes are only recomputed on request; until then their operation is reported
/// as <see cref="CamOperationResult.Stale"/> and NOT analyzed – an outdated path is never
/// shown or checked as if it were current.
/// </para>
/// </summary>
public sealed class CamPlanner
{
    public delegate ContourPreview PreviewGenerator(DesignShape shape, CamOperation operation, double materialThickness);

    /// <summary>Vector shapes with at most this many points are recomputed automatically.</summary>
    public const int AutoRecomputePointLimit = 1000;

    private sealed record CacheEntry(string Key, double X, double Y, ContourPreview? Preview, string? Error);

    private readonly PreviewGenerator _generator;
    private readonly Dictionary<Guid, CacheEntry> _cache = new();
    private readonly Dictionary<Guid, (string Key, IReadOnlyList<CamFinding> Findings)> _partCache = new();

    public CamPlanner(PreviewGenerator generator) => _generator = generator;

    /// <summary>Number of times the generator has been invoked (diagnostics/tests).</summary>
    public int GenerationCount { get; private set; }

    public void Clear() { _cache.Clear(); _partCache.Clear(); }

    /// <summary>True when the shape's toolpath is cheap enough to recompute on every change.</summary>
    public static bool IsCheap(DesignShape shape) =>
        shape.Kind != ShapeKind.VectorPath ||
        (shape.Contours?.Sum(c => c.Points?.Count ?? 0) ?? 0) <= AutoRecomputePointLimit;

    /// <summary>
    /// Reviews every CAM operation. Heavy shapes whose path is outdated are recomputed only
    /// when <paramref name="computeAll"/> is true or their id is in <paramref name="computeShapes"/>.
    /// </summary>
    public CamReport Review(CncDocument document, bool computeAll = false,
        IReadOnlyCollection<Guid>? computeShapes = null)
    {
        var results = new List<CamOperationResult>();
        var live = new HashSet<Guid>();
        // Geometry keys of every part with CAM, computed once per review for the part-to-part check.
        var geometryKeys = new Dictionary<Guid, string>();
        // A facing area is not a part: other paths may cross it (it is flattened first).
        foreach (var op in document.CamOperations.Where(o => o.Kind != CamOperationKind.Face))
            if (document.Shapes.FirstOrDefault(s => s.Id == op.ShapeId) is DesignShape s && !geometryKeys.ContainsKey(s.Id))
                geometryKeys[s.Id] = GeometryKey(s);
        // Material or fixtures outside the work area: no toolpath is computed until it is fixed.
        var placement = MachineSetupValidation.PlacementProblem(document);
        foreach (var op in document.CamOperations)
        {
            live.Add(op.ShapeId);
            var shape = document.Shapes.FirstOrDefault(s => s.Id == op.ShapeId);
            if (shape is null)
            {
                results.Add(new CamOperationResult(op, null, null, new[] {
                    new CamFinding(CamSeverity.Critical, "MissingShape",
                        "CAM-operationen saknar sitt ritobjekt och kan inte kontrolleras.", op.ShapeId) }));
                continue;
            }
            var findings = new List<CamFinding>();
            if (!document.HasMaterial)
            {
                findings.Add(new CamFinding(CamSeverity.Critical, "NoMaterial",
                    "Projektet har inget material ännu. Lägg till material under Egenskaper → Projekt och material – inga körbanor beräknas innan dess.", shape.Id));
                results.Add(new CamOperationResult(op, shape, null, findings));
                continue;
            }
            if (placement is not null)
            {
                findings.Add(new CamFinding(CamSeverity.Critical, "PlacementOutsideMachine",
                    placement + " Inga körbanor beräknas förrän det är rättat – flytta materialet eller ändra dess storlek.", shape.Id));
                results.Add(new CamOperationResult(op, shape, null, findings));
                continue;
            }
            if (CamValidation.GetError(op, document.MaterialThickness) is string settingsError)
            {
                findings.Add(new CamFinding(CamSeverity.Critical, "InvalidSettings",
                    settingsError + " Ingen verktygsbana beräknas – operationen är INTE kontrollerad.", shape.Id));
                results.Add(new CamOperationResult(op, shape, null, findings));
                continue;
            }
            findings.AddRange(CamOperationChecks.Analyze(op, shape, document.MaterialThickness));
            bool mayCompute = computeAll || IsCheap(shape) || (computeShapes?.Contains(shape.Id) ?? false);
            var entry = GetOrGenerate(shape, op, document.MaterialThickness, mayCompute);
            if (entry is null)
            {
                findings.Insert(0, new CamFinding(CamSeverity.Warning, "Stale",
                    "Körbanan är inaktuell efter en ändring. Klicka 'Beräkna körbanor'. " +
                    "Operationen är INTE kontrollerad.", shape.Id));
                results.Add(new CamOperationResult(op, shape, null, findings, Stale: true));
                continue;
            }
            if (entry.Preview is null)
            {
                findings.Insert(0, new CamFinding(CamSeverity.Critical, "NotAnalyzed",
                    $"Ingen verktygsbana kunde beräknas: {entry.Error} Operationen är INTE kontrollerad.", shape.Id));
                results.Add(new CamOperationResult(op, shape, null, findings));
                continue;
            }
            findings.AddRange(CamSafetyReview.Analyze(document, shape, op, entry.Preview));
            findings.AddRange(CamGeometryChecks.MaterialBounds(document, shape, op, entry.Preview));
            findings.AddRange(PartClearance(document, shape, op, entry.Preview, geometryKeys));
            if (entry.Preview.IsPocket) findings.AddRange(PocketFindings(entry.Preview, op, shape));
            var tabs = CamTabs.Plan(entry.Preview, op, document.MaterialThickness);
            findings.AddRange(TabFindings(op, shape, entry.Preview, tabs, document.MaterialThickness));
            results.Add(new CamOperationResult(op, shape, entry.Preview, findings, Tabs: tabs));
        }
        foreach (var stale in _cache.Keys.Where(k => !live.Contains(k)).ToList())
            _cache.Remove(stale);
        foreach (var stale in _partCache.Keys.Where(k => !live.Contains(k)).ToList())
            _partCache.Remove(stale);
        var (motion, motionFindings) = MotionPlanner.Plan(document, results);
        return new CamReport(results) { Motion = motion, GlobalFindings = motionFindings };
    }

    /// <summary>Returns null when the cached path is outdated and computing is not allowed.</summary>
    private CacheEntry? GetOrGenerate(DesignShape shape, CamOperation op, double thickness, bool mayCompute)
    {
        var key = Fingerprint(shape, op, thickness);
        bool known = _cache.TryGetValue(op.ShapeId, out var hit);
        if (known && hit!.Key == key)
        {
            if (hit.X == shape.X && hit.Y == shape.Y) return hit;
            // Pure move: contour offsets are translation-invariant, so shift the cached path
            // instead of recomputing it. Cheap even for very large imported shapes.
            var moved = hit with { X = shape.X, Y = shape.Y,
                Preview = hit.Preview is null ? null : Translate(hit.Preview, shape.X - hit.X, shape.Y - hit.Y) };
            _cache[op.ShapeId] = moved;
            return moved;
        }
        // A path that has never been computed (e.g. a just-opened project) is computed once.
        if (known && !mayCompute) return null;
        CacheEntry entry;
        GenerationCount++;
        try { entry = new CacheEntry(key, shape.X, shape.Y, CamDirection.Apply(_generator(shape, op, thickness), shape, op), null); }
        catch (Exception ex) { entry = new CacheEntry(key, shape.X, shape.Y, null, ex.Message); } // fail closed
        _cache[op.ShapeId] = entry;
        return entry;
    }

    /// <summary>
    /// Part-to-part check, cached per operation: it only depends on this path and the other
    /// parts' geometry and position, not on fixtures or stock placement.
    /// </summary>
    private IReadOnlyList<CamFinding> PartClearance(CncDocument doc, DesignShape shape, CamOperation op,
        ContourPreview preview, IReadOnlyDictionary<Guid, string> geometryKeys)
    {
        var others = geometryKeys.Where(k => k.Key != shape.Id).OrderBy(k => k.Key).ToList();
        var key = Fingerprint(shape, op, doc.MaterialThickness) + "@" + R(shape.X) + "," + R(shape.Y) + "#" +
                  string.Join(";", others.Select(k => k.Key + "=" + k.Value + ":" + doc.Shapes.FindIndex(s => s.Id == k.Key)));
        if (_partCache.TryGetValue(shape.Id, out var hit) && hit.Key == key) return hit.Findings;
        var parts = others
            .Select(k => doc.Shapes.First(s => s.Id == k.Key))
            .Select(s => (s, Label(doc, s)));
        var findings = CamGeometryChecks.PartClearance(shape, op, preview, parts);
        _partCache[shape.Id] = (key, findings);
        return findings;
    }

    private static IEnumerable<CamFinding> TabFindings(CamOperation op, DesignShape shape,
        ContourPreview preview, IReadOnlyList<TabSpan> tabs, double thickness)
    {
        if (!CamTabs.AppliesTo(op, thickness)) yield break;
        int paths = preview.Centerlines.Count > 0 ? preview.Centerlines.Count : 1;
        if (tabs.Count == 0)
        {
            yield return new CamFinding(CamSeverity.Warning, "NoTabsFit",
                $"Banan är för kort för hållflikar på {op.TabWidth:0.###} mm – delen kan lossna. " +
                "Minska flikbredden eller fäst delen på annat sätt.", shape.Id);
            yield break;
        }
        int pathsWithTabs = tabs.Select(t => t.PathIndex).Distinct().Count();
        yield return new CamFinding(CamSeverity.Info, "TabsPlaced",
            $"{tabs.Count} hållflikar ({op.TabWidth:0.###} mm breda, {op.TabHeight:0.###} mm höga) på " +
            $"{pathsWithTabs} av {paths} bana/banor.", shape.Id);
        if (tabs.GroupBy(t => t.PathIndex).Any(g => g.Count() < op.TabCount))
            yield return new CamFinding(CamSeverity.Info, "FewerTabs",
                $"Färre än {op.TabCount} flikar fick plats på en eller flera korta banor.", shape.Id);
    }

    /// <summary>
    /// Rest material: uncut area between the loops is a warning (stepover too large for the shape);
    /// inside corners keep the tool's radius, which is reported as information.
    /// </summary>
    private static IEnumerable<CamFinding> PocketFindings(ContourPreview p, CamOperation op, DesignShape shape)
    {
        // Small tolerance for polygon approximation of arcs.
        double tolerance = Math.Max(0.5, 0.0005 * shape.Width * shape.Height);
        if (p.PocketRestArea > tolerance)
            yield return new CamFinding(CamSeverity.Warning, "PocketRest",
                $"Det blir kvar ca {p.PocketRestArea:0.#} mm² material mellan varven i fickan. Minska stegningen " +
                $"(nu {op.StepoverPercent:0.#} % av Ø {op.ToolDiameter:0.###} mm).", shape.Id);
        if (p.PocketCornerArea > 0.05)
            yield return new CamFinding(CamSeverity.Info, "PocketCorners",
                $"Inre hörn i fickan blir rundade med fräsens radie {op.ToolDiameter / 2:0.###} mm " +
                $"(ca {p.PocketCornerArea:0.#} mm² kvar i hörnen).", shape.Id);
        if (op.StepoverPercent > 50)
            yield return new CamFinding(CamSeverity.Info, "PocketStepover",
                $"Stegning {op.StepoverPercent:0.#} % är hög; i trä används ofta 30–50 % av diametern.", shape.Id);
    }

    /// <summary>Human-readable name used in messages, e.g. "Rektangel 2".</summary>
    public static string Label(CncDocument doc, DesignShape shape)
    {
        string kind = shape.Kind switch
        {
            ShapeKind.Rectangle => "Rektangel", ShapeKind.Ellipse => "Ellips", _ => "Vektorform"
        };
        return $"{kind} {doc.Shapes.IndexOf(shape) + 1}";
    }

    /// <summary>Shape geometry including position (no CAM settings).</summary>
    private static string GeometryKey(DesignShape s) =>
        string.Join("|", s.Kind, R(s.X), R(s.Y), R(s.Width), R(s.Height), ContourHash(s));

    private static int ContourHash(DesignShape shape)
    {
        var hash = new HashCode();
        if (shape.Contours is not null)
            foreach (var c in shape.Contours)
            {
                hash.Add(c.Closed);
                hash.Add(c.Points?.Count ?? -1);
                if (c.Points is null) continue;
                foreach (var p in c.Points) { hash.Add(p.X); hash.Add(p.Y); }
            }
        return hash.ToHashCode();
    }

    private static ContourPreview Translate(ContourPreview p, double dx, double dy)
    {
        static IReadOnlyList<CamPoint> Shift(IReadOnlyList<CamPoint> path, double dx, double dy) =>
            path.Select(pt => new CamPoint(pt.X + dx, pt.Y + dy)).ToList();
        // 'with' keeps every other property (pocket flags and areas); only geometry is shifted.
        return p with
        {
            Centerline = Shift(p.Centerline, dx, dy),
            Centerlines = p.Centerlines.Select(c => Shift(c, dx, dy)).ToList(),
            PocketCenterRegion = p.PocketCenterRegion.Select(c => Shift(c, dx, dy)).ToList()
        };
    }

    /// <summary>
    /// Everything that affects the toolpath's shape, and nothing else. Position (X/Y) is
    /// deliberately excluded: a pure move translates the cached path instead.
    /// </summary>
    public static string Fingerprint(DesignShape shape, CamOperation op, double thickness)
    {
        return string.Join("|", shape.Kind, R(shape.Width), R(shape.Height),
            shape.Contours?.Count ?? -1, ContourHash(shape),
            op.Kind, R(op.ToolDiameter), R(op.TotalDepth), R(op.DepthPerPass), op.UseTabs,
            op.TabCount, R(op.TabWidth), R(op.TabHeight), R(op.StepoverPercent), op.Direction, R(thickness));
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
