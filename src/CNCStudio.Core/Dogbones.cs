using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>
/// Dogbones i innerhörn: a round cutter leaves a radius in every sharp corner it cannot get into – a square tenon
/// then does not go into a milled hole. At each such corner the toolpath makes a short excursion straight into the
/// corner, along the bisector, until the cutter's edge reaches the sharp corner, and back. The corner gets a round
/// "bone" just past it and the square part fits.
/// <para>
/// Which corners: where the path turns away from the wall (the material that stays) – the hole's corners for an
/// inside contour or a pocket, the part's inner corners for an outside contour. Corners rounded by the offset
/// itself turn towards the wall and are left as they are. Only real corners (a turn of at least
/// <see cref="MinTurnDeg"/>°) get a bone, so curves flattened into short segments are not touched.
/// </para>
/// </summary>
public static class Dogbones
{
    /// <summary>A smaller change of direction is a curve, not a corner.</summary>
    public const double MinTurnDeg = 25;

    public static bool AppliesTo(CamOperation op) =>
        op.Dogbones && op.Kind is CamOperationKind.Inside or CamOperationKind.Outside or CamOperationKind.Pocket;

    /// <summary>Adds the bones to every path whose wall side is known.</summary>
    public static ContourPreview Apply(ContourPreview p, DesignShape shape, CamOperation op)
    {
        if (!AppliesTo(op) || p.IsDrill || p.IsFace || p.IsVCarve || p.Is3D) return p;
        double r = op.ToolDiameter / 2;
        var paths = p.Centerlines.Count > 0 ? p.Centerlines : new[] { p.Centerline };
        var outline = CamGeometryChecks.Outline(shape);
        var result = paths.Select(path => CamDirection.WallOnRight(path, p, shape, op, outline) is bool right ? Add(path, right, r) : path).ToList();
        return p with { Centerline = result[0], Centerlines = result };
    }

    /// <summary>
    /// The path with a bone at every corner that turns away from the wall. <paramref name="wallOnRight"/> is in
    /// machine coordinates (Y up) as <see cref="CamDirection"/> gives it; the path is in design coordinates (Y down).
    /// </summary>
    public static IReadOnlyList<CamPoint> Add(IReadOnlyList<CamPoint> path, bool wallOnRight, double radius)
    {
        bool closed = path.Count > 3 && PathMath.Distance(path[0], path[^1]) < 1e-6;
        var pts = closed ? path.Take(path.Count - 1).ToList() : path.ToList();
        int n = pts.Count;
        if (n < 3 || radius <= 0) return path;
        var output = new List<CamPoint>(n + 8);
        double minTurn = MinTurnDeg * Math.PI / 180;
        for (int i = 0; i < n; i++)
        {
            var at = pts[i];
            output.Add(at);
            if (!closed && (i == 0 || i == n - 1)) continue;
            var a = pts[(i - 1 + n) % n]; var b = pts[(i + 1) % n];
            // Machine coordinates (mirror Y) for the turn and the wall side.
            double d1x = at.X - a.X, d1y = -(at.Y - a.Y), d2x = b.X - at.X, d2y = -(b.Y - at.Y);
            double l1 = Math.Sqrt(d1x * d1x + d1y * d1y), l2 = Math.Sqrt(d2x * d2x + d2y * d2y);
            if (l1 < 1e-6 || l2 < 1e-6) continue;
            d1x /= l1; d1y /= l1; d2x /= l2; d2y /= l2;
            double cross = d1x * d2y - d1y * d2x, dot = d1x * d2x + d1y * d2y;
            double turn = Math.Atan2(Math.Abs(cross), dot);
            if (turn < minTurn || turn > Math.PI - 0.05) continue;
            // A left turn (cross > 0) goes away from a wall on the right, and the other way round.
            if (wallOnRight ? cross <= 0 : cross >= 0) continue;
            // Normals towards the wall; the sharp corner lies along their bisector.
            double s = wallOnRight ? 1 : -1;
            double n1x = s * d1y, n1y = -s * d1x, n2x = s * d2y, n2y = -s * d2x;
            double bx = n1x + n2x, by = n1y + n2y, bl = Math.Sqrt(bx * bx + by * by);
            if (bl < 1e-9) continue;
            bx /= bl; by /= bl;
            double cos = bx * n1x + by * n1y;                    // the corner is radius / cos away
            if (cos < 1e-3) continue;
            double reach = radius / cos - radius;                 // the cutter's edge just touches the corner
            if (reach < 0.01) continue;
            var bone = new CamPoint(at.X + bx * reach, at.Y - by * reach);   // back to design coordinates
            output.Add(bone);
            output.Add(at);
        }
        if (closed) output.Add(output[0]);
        return output;
    }
}
