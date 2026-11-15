using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace CNCStudio.Core;

/// <summary>
/// A 3D model (STL) placed on the material: the design shape is its box seen from above (Width × Height), and the
/// model is carved <see cref="DepthMm"/> deep – its highest point at the material's surface. The STL is kept in the
/// project (so it opens on another computer too).
/// </summary>
public sealed class Model3D
{
    public string Name { get; set; } = "";
    public string StlBase64 { get; set; } = "";
    /// <summary>A short hash of the STL – part of the toolpath cache key.</summary>
    public string Hash { get; set; } = "";
    public double DepthMm { get; set; } = 10;
    /// <summary>Quarter turns (counter-clockwise, seen from above) of the model on the material.</summary>
    public int Quarter { get; set; }

    [JsonIgnore] private List<(Vector3 A, Vector3 B, Vector3 C)>? _triangles;

    public static Model3D FromStl(byte[] stl, string name, double depthMm)
    {
        var tris = Stl.Parse(stl);
        return new Model3D
        {
            Name = name, StlBase64 = Convert.ToBase64String(stl), DepthMm = depthMm,
            Hash = Convert.ToHexString(SHA256.HashData(stl))[..16], _triangles = tris,
        };
    }

    /// <summary>A copy turned by more quarter turns (counter-clockwise; −1 = clockwise).</summary>
    public Model3D Turned(int quarters)
    {
        var c = (Model3D)MemberwiseClone();
        c.Quarter = ((Quarter + quarters) % 4 + 4) % 4;
        return c;
    }

    public IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> Triangles() => _triangles ??= Stl.Parse(Convert.FromBase64String(StlBase64));

    /// <summary>The model's size along X, Y (after the quarter turns) and Z, in its own units.</summary>
    public (double X, double Y, double Z) Size()
    {
        var t = Triangles();
        var pts = t.SelectMany(x => new[] { x.A, x.B, x.C }).Select(p => Turn(p, Quarter)).ToList();
        return (pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y), pts.Max(p => p.Z) - pts.Min(p => p.Z));
    }

    public static Vector3 Turn(Vector3 p, int quarter) => ((quarter % 4 + 4) % 4) switch
    {
        1 => new Vector3(-p.Y, p.X, p.Z), 2 => new Vector3(-p.X, -p.Y, p.Z), 3 => new Vector3(p.Y, -p.X, p.Z), _ => p,
    };
}

/// <summary>STL files, binary and ASCII (the two kinds every 3D program writes).</summary>
public static class Stl
{
    public const int MaxTriangles = 2_000_000;

    public static List<(Vector3 A, Vector3 B, Vector3 C)> Parse(byte[] data)
    {
        if (data.Length >= 84)
        {
            uint count = BitConverter.ToUInt32(data, 80);
            if (count > 0 && 84 + count * 50L == data.Length)
            {
                if (count > MaxTriangles) throw new InvalidDataException($"Modellen har {count} trianglar – högst {MaxTriangles}. Förenkla den i 3D-programmet.");
                var list = new List<(Vector3, Vector3, Vector3)>((int)count);
                for (int i = 0; i < count; i++)
                {
                    int o = 84 + i * 50 + 12;
                    Vector3 V(int k) => new(BitConverter.ToSingle(data, o + k * 12), BitConverter.ToSingle(data, o + k * 12 + 4), BitConverter.ToSingle(data, o + k * 12 + 8));
                    list.Add((V(0), V(1), V(2)));
                }
                return Check(list);
            }
        }
        // ASCII: "vertex x y z" three at a time.
        var text = Encoding.ASCII.GetString(data);
        if (!text.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Filen är ingen STL-fil.");
        var verts = new List<Vector3>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) throw new InvalidDataException("Trasig rad i STL-filen: " + line);
            verts.Add(new Vector3(float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture), float.Parse(p[3], CultureInfo.InvariantCulture)));
        }
        if (verts.Count / 3 > MaxTriangles) throw new InvalidDataException("Modellen har för många trianglar – förenkla den.");
        var tris = new List<(Vector3, Vector3, Vector3)>();
        for (int i = 0; i + 2 < verts.Count; i += 3) tris.Add((verts[i], verts[i + 1], verts[i + 2]));
        return Check(tris);
    }

    private static List<(Vector3, Vector3, Vector3)> Check(List<(Vector3 A, Vector3 B, Vector3 C)> t)
    {
        t.RemoveAll(x => !Finite(x.A) || !Finite(x.B) || !Finite(x.C));
        if (t.Count == 0) throw new InvalidDataException("STL-filen innehåller inga trianglar.");
        return t;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>A binary STL (for tests and samples).</summary>
    public static byte[] Write(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> tris)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(new byte[80]); w.Write((uint)tris.Count);
        foreach (var (a, b, c) in tris)
        {
            w.Write(0f); w.Write(0f); w.Write(1f);
            foreach (var v in new[] { a, b, c }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write((ushort)0);
        }
        return ms.ToArray();
    }
}

/// <summary>
/// The model's top surface on a grid over the shape (design coordinates, Y down from the back): for each cell the
/// work Z of the highest triangle above it – 0 at the model's top, −Depth at its bottom. Cells the model does not
/// cover are carved to −Depth (the box around the model is cleared).
/// </summary>
public sealed class Heightmap
{
    public double X0 { get; }
    public double Y0 { get; }
    public double Cell { get; }
    public int Nx { get; }
    public int Ny { get; }
    public float[] Z { get; }

    private Heightmap(double x0, double y0, double cell, int nx, int ny, float[] z) { X0 = x0; Y0 = y0; Cell = cell; Nx = nx; Ny = ny; Z = z; }

    /// <summary>Height at a point; outside the shape the material is untouched (0).</summary>
    public double At(double x, double y)
    {
        int i = (int)Math.Floor((x - X0) / Cell), j = (int)Math.Floor((y - Y0) / Cell);
        return i < 0 || j < 0 || i >= Nx || j >= Ny ? 0 : Z[j * Nx + i];
    }

    public static double CellFor(DesignShape s) => Math.Clamp(Math.Min(s.Width, s.Height) / 600, 0.1, 1.0);

    public static Heightmap Build(DesignShape s)
    {
        var m = s.Model ?? throw new ArgumentException("Formen är ingen 3D-modell.");
        double cell = CellFor(s);
        int nx = Math.Max(1, (int)Math.Ceiling(s.Width / cell)), ny = Math.Max(1, (int)Math.Ceiling(s.Height / cell));
        if ((long)nx * ny > 8_000_000) throw new ArgumentException("Modellen är för stor för höjdkartan.");
        var tris = m.Triangles();
        var pts = tris.SelectMany(t => new[] { t.A, t.B, t.C }).Select(p => Model3D.Turn(p, m.Quarter)).ToList();
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y), minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);
        double sx = s.Width / Math.Max(maxX - minX, 1e-6), sy = s.Height / Math.Max(maxY - minY, 1e-6), sz = m.DepthMm / Math.Max(maxZ - minZ, 1e-6);
        var z = new float[nx * ny];
        Array.Fill(z, (float)-m.DepthMm);
        // Design X → right, design Y → down from the back (= the model's +Y away from the operator).
        (double X, double Y, double Z) Map(Vector3 p) { var q = Model3D.Turn(p, m.Quarter); return ((q.X - minX) * sx, (maxY - q.Y) * sy, (q.Z - maxZ) * sz); }
        foreach (var t in tris)
        {
            var a = Map(t.A); var b = Map(t.B); var c = Map(t.C);
            double det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Math.Abs(det) < 1e-12) continue;               // seen edge-on from above
            int i0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)) / cell)), i1 = Math.Min(nx - 1, (int)Math.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) / cell));
            int j0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)) / cell)), j1 = Math.Min(ny - 1, (int)Math.Floor(Math.Max(a.Y, Math.Max(b.Y, c.Y)) / cell));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    double px = (i + 0.5) * cell, py = (j + 0.5) * cell;
                    double l1 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / det;
                    double l2 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / det;
                    double l3 = 1 - l1 - l2;
                    if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9) continue;
                    float h = (float)(l1 * a.Z + l2 * b.Z + l3 * c.Z);
                    int k = j * nx + i;
                    if (h > z[k]) z[k] = h;
                }
        }
        return new Heightmap(s.X, s.Y, cell, nx, ny, z);
    }
}
