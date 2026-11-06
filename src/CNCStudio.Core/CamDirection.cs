using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// Sets the travel direction of each closed toolpath for climb or conventional milling.
/// <para>
/// Rule (clockwise spindle, M3, seen from above): climb milling = the finished wall is on the tool's
/// RIGHT in the direction of travel; conventional = on the LEFT. Evaluated in machine coordinates
/// (Y towards the back). The design view has Y downwards, so it is mirrored before deciding.
/// </para>
/// </summary>
public static class CamDirection
{
    public static ContourPreview Apply(ContourPreview p, DesignShape shape, CamOperation op)
    {
        if (op.Direction == MillingDirection.Default) return p;
        bool wantRight = op.Direction == MillingDirection.Climb;
        var paths = p.Centerlines.Count > 0 ? p.Centerlines : new[] { p.Centerline };
        var outline = CamGeometryChecks.Outline(shape);
        var result = paths.Select(path =>
        {
            bool? wallRight = p.IsPocket ? PocketWallOnRight(path) : ContourWallOnRight(path, shape, op, outline);
            return wallRight is bool r && r != wantRight ? Reverse(path) : path;
        }).ToList();
        return p with { Centerline = result[0], Centerlines = result };
    }

    /// <summary>Signed area in machine coordinates (Y up): positive = counter-clockwise.</summary>
    public static double MachineArea(IReadOnlyList<CamPoint> path)
    {
        double s = 0;
        for (int i = 0; i + 1 < path.Count; i++) s += path[i].X * path[i + 1].Y - path[i + 1].X * path[i].Y;
        return s / 2 * -1; // design Y is downwards: mirror
    }

    /// <summary>
    /// Pocket loops come from Clipper: outer boundaries (wall outside the loop) and loops around
    /// islands (wall inside). Which one a loop is follows from its orientation in design coordinates
    /// (Clipper output: outer positive, holes negative).
    /// </summary>
    private static bool? PocketWallOnRight(IReadOnlyList<CamPoint> path)
    {
        double machineArea = MachineArea(path);
        if (Math.Abs(machineArea) < 1e-9) return null;
        bool wallOutside = machineArea < 0; // design-positive (outer) is machine-negative
        bool ccw = machineArea > 0;
        // Travelling CCW keeps the outside on the right; CW keeps the inside on the right.
        return wallOutside ? ccw : !ccw;
    }

    /// <summary>
    /// Contour paths lie one tool radius from the wall: probe both sides of a segment and see which
    /// side is the material that stays (the part for outside contours, the surroundings otherwise).
    /// </summary>
    private static bool? ContourWallOnRight(IReadOnlyList<CamPoint> path, DesignShape shape, CamOperation op,
        IReadOnlyList<IReadOnlyList<CamPoint>> outline)
    {
        double probe = op.ToolDiameter * 0.75; // past the wall (radius = 0.5 × diameter)
        for (int i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i]; var b = path[i + 1];
            double len = PathMath.Distance(a, b);
            if (len < 0.05) continue;
            double tx = (b.X - a.X) / len, ty = (b.Y - a.Y) / len;
            var mid = new CamPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var side1 = new CamPoint(mid.X - ty * probe, mid.Y + tx * probe);
            var side2 = new CamPoint(mid.X + ty * probe, mid.Y - tx * probe);
            bool keep1 = Keeps(side1), keep2 = Keeps(side2);
            if (keep1 == keep2) continue; // ambiguous here, try another segment
            var wall = keep1 ? side1 : side2;
            // Machine coordinates: mirror Y. Right of travel ⇔ cross(travel, toWall) < 0.
            double mtx = tx, mty = -ty, wx = wall.X - mid.X, wy = -(wall.Y - mid.Y);
            return mtx * wy - mty * wx < 0;
        }
        return null;

        bool Keeps(CamPoint q)
        {
            bool inShape = CamGeometryChecks.Inside(q, outline);
            return op.Kind == CamOperationKind.Outside ? inShape : !inShape;
        }
    }

    /// <summary>Same closed loop, same start point, opposite direction.</summary>
    private static IReadOnlyList<CamPoint> Reverse(IReadOnlyList<CamPoint> path)
    {
        var r = new List<CamPoint>(path.Count) { path[0] };
        for (int i = path.Count - 2; i >= 0; i--) r.Add(path[i]);
        return r;
    }
}
