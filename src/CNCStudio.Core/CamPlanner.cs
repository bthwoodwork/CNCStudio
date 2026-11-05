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
    ContourPreview? Preview, IReadOnlyList<CamFinding> Findings, bool Stale = false)
{
    /// <summary>True only when a toolpath exists and the available checks have run on it.</summary>
    public bool Analyzed => Preview is not null;
    public CamSeverity? Worst => Findings.Count == 0 ? null : Findings.Min(f => f.Severity);
}

public sealed record CamReport(IReadOnlyList<CamOperationResult> Operations)
{
    public static readonly CamReport Empty = new(Array.Empty<CamOperationResult>());
    public IEnumerable<CamFinding> AllFindings => Operations.SelectMany(o => o.Findings);
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
    public static IReadOnlyList<CamFinding> Analyze(CamOperation op, DesignShape shape, double materialThickness)
    {
        var list = new List<CamFinding>();
        var id = shape.Id;
        if (op.DepthPerPass > op.ToolDiameter + 1e-9)
            list.Add(new CamFinding(CamSeverity.Warning, "DeepPass",
                $"Djup per pass ({F(op.DepthPerPass)} mm) är större än fräsdiametern ({F(op.ToolDiameter)} mm). " +
                "Risk för fräsbrott; ett vanligt riktvärde i trä är högst 1 × diametern.", id));
        bool through = op.TotalDepth >= materialThickness - 1e-6;
        bool contour = op.Kind is CamOperationKind.Outside or CamOperationKind.Inside;
        if (through)
        {
            list.Add(new CamFinding(CamSeverity.Info, "ThroughCut",
                $"Genomskärning ({F(op.TotalDepth)} mm i {F(materialThickness)} mm material). " +
                "Använd offerskiva och kontrollera Z-nollpunkten.", id));
            if (contour && !op.UseTabs)
                list.Add(new CamFinding(CamSeverity.Warning, "LoosePart",
                    "Genomskärning utan hållflikar: den utskurna delen kan lossna och slungas iväg av fräsen.", id));
            if (contour && op.UseTabs)
                list.Add(new CamFinding(CamSeverity.Warning, "TabsNotApplied",
                    "Hållflikar är valda men finns ännu inte i banan – den skär runt helt och delen kan lossna.", id));
        }
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

    public CamPlanner(PreviewGenerator generator) => _generator = generator;

    /// <summary>Number of times the generator has been invoked (diagnostics/tests).</summary>
    public int GenerationCount { get; private set; }

    public void Clear() => _cache.Clear();

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
            results.Add(new CamOperationResult(op, shape, entry.Preview, findings));
        }
        foreach (var stale in _cache.Keys.Where(k => !live.Contains(k)).ToList())
            _cache.Remove(stale);
        return new CamReport(results);
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
        try { entry = new CacheEntry(key, shape.X, shape.Y, _generator(shape, op, thickness), null); }
        catch (Exception ex) { entry = new CacheEntry(key, shape.X, shape.Y, null, ex.Message); } // fail closed
        _cache[op.ShapeId] = entry;
        return entry;
    }

    private static ContourPreview Translate(ContourPreview p, double dx, double dy)
    {
        static IReadOnlyList<CamPoint> Shift(IReadOnlyList<CamPoint> path, double dx, double dy) =>
            path.Select(pt => new CamPoint(pt.X + dx, pt.Y + dy)).ToList();
        return new ContourPreview(Shift(p.Centerline, dx, dy), p.PassDepths, p.Warning)
        { Centerlines = p.Centerlines.Select(c => Shift(c, dx, dy)).ToList() };
    }

    /// <summary>
    /// Everything that affects the toolpath's shape, and nothing else. Position (X/Y) is
    /// deliberately excluded: a pure move translates the cached path instead.
    /// </summary>
    public static string Fingerprint(DesignShape shape, CamOperation op, double thickness)
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
        return string.Join("|", shape.Kind, R(shape.Width), R(shape.Height),
            shape.Contours?.Count ?? -1, hash.ToHashCode(),
            op.Kind, R(op.ToolDiameter), R(op.TotalDepth), R(op.DepthPerPass), op.UseTabs, R(thickness));
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
