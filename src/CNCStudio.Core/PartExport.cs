using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Clipper2Lib;

namespace CNCStudio.Core;

/// <summary>
/// Exportera STL: the selected parts as solids – their outline extruded to a thickness (normally the material's) –
/// for a 3D printer or another 3D program. Shapes with a through inside cut (Invändig) that lie on a part become
/// holes in it. Normally the drawn shape is used, sharp corners and all: dogbones are only for the router.
/// <see cref="Options.AsMilled"/> instead gives the part as the cutter leaves it – the cut's own path with its
/// radius, dogbones included – where a toolpath has been computed.
/// <para>Coordinates: mm, X right, Y towards the back from the material's front-left corner (as on the machine), Z up from 0.</para>
/// </summary>
public static class PartExport
{
    public sealed record Options(double ThicknessMm, bool AsMilled = false);

    /// <summary>One part: its name and its outline (outer loops counter-clockwise, holes clockwise, Y up).</summary>
    public sealed record Part(string Name, PathsD Region);

    private const int Decimals = 4;

    /// <summary>The parts among <paramref name="shapes"/>: every shape that is not a hole, pocket or engraving, with the holes on it cut out.</summary>
    public static IReadOnlyList<Part> Parts(CncDocument doc, CamReport report, IReadOnlyCollection<DesignShape> shapes, Options o)
    {
        CamOperation? Op(DesignShape s) => doc.CamOperations.FirstOrDefault(c => c.ShapeId == s.Id);
        double baseY = doc.HasMaterial ? doc.MaterialHeight : shapes.Max(s => s.Y + s.Height);
        PathsD Up(IEnumerable<IReadOnlyList<CamPoint>> loops) =>
            new(loops.Select(l => new PathD(l.Select(p => new PointD(p.X, baseY - p.Y)))));
        PathsD Region(DesignShape s) => Clipper.Union(Up(Outline(s)), new PathsD(), FillRule.EvenOdd, Decimals);
        // As milled: what is inside the cutter's path, with or without the band one cutter wide that it takes away.
        // A part (outside cut) is what the path encloses minus the band; a hole (inside cut) is that plus the band –
        // so the part keeps a radius in its inner corners and the hole in its corners, unless a dogbone goes in.
        PathsD? Milled(DesignShape s, CamOperation op, bool hole)
        {
            var paths = report.For(s.Id)?.Preview is { } p ? (p.Centerlines.Count > 0 ? p.Centerlines : new[] { p.Centerline }) : null;
            if (paths is null || paths.All(x => x.Count < 3)) return null;
            var centre = Up(paths.Where(x => x.Count >= 3));
            var inside = Clipper.Union(centre, new PathsD(), FillRule.EvenOdd, Decimals);
            var band = Clipper.InflatePaths(centre, op.ToolDiameter / 2, JoinType.Round, EndType.Joined, 2, Decimals);
            return hole ? Clipper.Union(inside, band, FillRule.NonZero, Decimals) : Clipper.Difference(inside, band, FillRule.NonZero, Decimals);
        }

        var holes = new PathsD();
        foreach (var s in shapes)
            if (Op(s) is { Kind: CamOperationKind.Inside } op && op.TotalDepth >= doc.MaterialThickness - 1e-6)
                holes.AddRange(o.AsMilled && Milled(s, op, hole: true) is { } m ? m : Region(s));

        var parts = new List<Part>();
        int n = 0;
        foreach (var s in shapes)
        {
            var op = Op(s);
            if (op is not null && op.Kind is not (CamOperationKind.Outside or CamOperationKind.Face)) continue;
            if (s.Model is not null) continue;
            var region = Region(s);
            if (o.AsMilled && op is { Kind: CamOperationKind.Outside } && Milled(s, op, hole: false) is { } m) region = m;
            if (holes.Count > 0) region = Clipper.Difference(region, holes, FillRule.NonZero, Decimals);
            // No points in the middle of a straight edge: the top and bottom would not meet the walls there.
            region = new PathsD(region.Select(p => Clipper.TrimCollinear(p, Decimals)).Where(p => p.Count >= 3 && Math.Abs(Clipper.Area(p)) > 0.01));
            if (region.Count == 0) continue;
            parts.Add(new Part($"Del {++n}", region));
        }
        return parts;
    }

    private static IEnumerable<IReadOnlyList<CamPoint>> Outline(DesignShape s) => s.Kind switch
    {
        ShapeKind.Ellipse => new[] { (IReadOnlyList<CamPoint>)Enumerable.Range(0, 144).Select(i =>
            new CamPoint(s.X + s.Width / 2 * (1 + Math.Cos(2 * Math.PI * i / 144)), s.Y + s.Height / 2 * (1 + Math.Sin(2 * Math.PI * i / 144)))).ToList() },
        _ => CamGeometryChecks.Outline(s)
    };

    /// <summary>The triangles of a part extruded from Z 0 to <paramref name="thickness"/>, facing outwards.</summary>
    public static List<(double[] A, double[] B, double[] C)> Solid(PathsD region, double thickness)
    {
        var tris = new List<(double[], double[], double[])>();
        if (Clipper.Triangulate(region, Decimals, out var flat, true) != TriangulateResult.success)
            throw new InvalidOperationException("Delen kunde inte delas upp i trianglar (korsande linjer?). Kontrollera vektorerna.");
        foreach (var t in flat)
        {
            if (t.Count != 3) continue;
            var (a, b, c) = (t[0], t[1], t[2]);
            if (Clipper.Area(t) < 0) (b, c) = (c, b);                      // counter-clockwise seen from above
            tris.Add((V(a, thickness), V(b, thickness), V(c, thickness)));   // top, normal up
            tris.Add((V(a, 0), V(c, 0), V(b, 0)));                           // bottom, normal down
        }
        // Walls: material is on the left of each loop (outer loops CCW, holes CW), so the outside is on the right.
        foreach (var loop in region)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i]; var b = loop[(i + 1) % loop.Count];
                if (Math.Abs(a.x - b.x) < 1e-9 && Math.Abs(a.y - b.y) < 1e-9) continue;
                tris.Add((V(a, 0), V(b, 0), V(b, thickness)));
                tris.Add((V(a, 0), V(b, thickness), V(a, thickness)));
            }
        }
        return tris;
    }

    private static double[] V(PointD p, double z) => new[] { p.x, p.y, z };

    /// <summary>Binary STL with the given triangles (normals computed from the winding).</summary>
    public static byte[] Stl(IEnumerable<(double[] A, double[] B, double[] C)> triangles, string name)
    {
        var list = triangles.ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var header = new byte[80];
        Encoding.ASCII.GetBytes(("CNC Studio " + name).PadRight(80)[..80]).CopyTo(header, 0);
        w.Write(header);
        w.Write((uint)list.Count);
        foreach (var (a, b, c) in list)
        {
            double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx, l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (l > 0) { nx /= l; ny /= l; nz /= l; }
            w.Write((float)nx); w.Write((float)ny); w.Write((float)nz);
            foreach (var v in new[] { a, b, c }) { w.Write((float)v[0]); w.Write((float)v[1]); w.Write((float)v[2]); }
            w.Write((ushort)0);
        }
        w.Flush();
        return ms.ToArray();
    }
}
