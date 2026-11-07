using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core;

public enum CamTreeStatus { Ok, Info, Warning, Critical, Stale, NoPath }

/// <summary>One operation in the tree: which object, what kind, how deep, and its check status.</summary>
public sealed record CamTreeOperation(int OperationIndex, CamOperation Operation, DesignShape Shape, string ShapeLabel,
    string KindLabel, string Detail, CamTreeStatus Status, IReadOnlyList<CamFinding> Findings, double CuttingMinutes);

/// <summary>One program: every operation with the same tool, in run order (no tool changer).</summary>
public sealed record CamTreeProgram(int Number, string ToolLabel, IReadOnlyList<CamTreeOperation> Operations)
{
    public double CuttingMinutes => Operations.Sum(o => o.CuttingMinutes);
    public CamTreeStatus Status => Operations.Count == 0 ? CamTreeStatus.Ok : Operations.Max(o => o.Status);
}

/// <summary>
/// The project's CAM as a tree: Program 1 … n (one per tool, in the order they run) with their
/// operations, plus the objects that have no operation yet. Built from the current CAM report.
/// </summary>
public sealed record CamTree(IReadOnlyList<CamTreeProgram> Programs, IReadOnlyList<(DesignShape Shape, string Label)> Unassigned)
{
    public static string KindLabel(CamOperationKind kind) => kind switch
    {
        CamOperationKind.Outside => "Kontur ut", CamOperationKind.Inside => "Kontur in",
        CamOperationKind.Pocket => "Ficka", CamOperationKind.Drill => "Borrning", _ => kind.ToString()
    };

    public static CamTree Build(CncDocument doc, CamReport report)
    {
        var minutes = report.Operations.Count == 0
            ? new Dictionary<int, double>()
            : RunTimeEstimator.Estimate(doc, report).MinutesPerOperation;
        var order = report.Motion.OperationOrder.ToList();
        int Rank(int i)
        {
            int k = order.IndexOf(i);
            if (k >= 0) return k;
            // Not planned (error or stale): same rule as the planner – outside contours last, else document order.
            return 100_000 + (report.Operations[i].Operation.Kind == CamOperationKind.Outside ? 50_000 : 0) + i;
        }

        var items = new List<(CamTreeOperation Item, (string, double, ToolKind) Tool, int Rank)>();
        for (int i = 0; i < report.Operations.Count; i++)
        {
            var r = report.Operations[i];
            if (r.Shape is not DesignShape shape) continue;
            var op = r.Operation;
            var status = r.Stale ? CamTreeStatus.Stale
                : r.Findings.Any(f => f.Severity == CamSeverity.Critical) ? CamTreeStatus.Critical
                : !r.Analyzed ? CamTreeStatus.NoPath
                : r.Findings.Any(f => f.Severity == CamSeverity.Warning) ? CamTreeStatus.Warning
                : CamTreeStatus.Ok;
            var item = new CamTreeOperation(i, op, shape, CamPlanner.Label(doc, shape), KindLabel(op.Kind), Detail(op, doc),
                status, r.Findings, minutes.GetValueOrDefault(i));
            items.Add((item, GCodeGenerator.ToolKey(op), Rank(i)));
        }
        var programs = items.GroupBy(x => x.Tool)
            .OrderBy(g => g.Min(x => x.Rank))
            .Select((g, n) => new CamTreeProgram(n + 1, GCodeGenerator.ToolLabel(g.First().Item.Operation),
                g.OrderBy(x => x.Rank).Select(x => x.Item).ToList()))
            .ToList();
        var withCam = report.Operations.Select(o => o.Operation.ShapeId).ToHashSet();
        var unassigned = doc.Shapes.Where(s => !withCam.Contains(s.Id)).Select(s => (s, CamPlanner.Label(doc, s))).ToList();
        return new CamTree(programs, unassigned);
    }

    private static string Detail(CamOperation op, CncDocument doc)
    {
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var text = $"{F(op.TotalDepth)} mm";
        bool through = op.TotalDepth >= doc.MaterialThickness - 1e-6;
        if (op.Kind is CamOperationKind.Outside or CamOperationKind.Inside && op.UseTabs && through)
            text += $" · {op.TabCount} flikar";
        if (through) text += " · genom";
        return text;
    }
}
