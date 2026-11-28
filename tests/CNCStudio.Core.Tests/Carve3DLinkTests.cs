using System.Numerics;
using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

public class Carve3DLinkTests
{
    /// <summary>Two pyramids side by side with a deep valley between: roughing cuts many separate runs.</summary>
    private static DesignShape Twin()
    {
        var t = new List<(Vector3, Vector3, Vector3)>();
        void Pyramid(float ox)
        {
            var a = new Vector3(ox, 0, 0); var b = new Vector3(ox + 50, 0, 0); var c = new Vector3(ox + 50, 50, 0); var d = new Vector3(ox, 50, 0);
            var top = new Vector3(ox + 25, 25, 20);
            t.AddRange(new[] { (a, b, top), (b, c, top), (c, d, top), (d, a, top), (a, c, b), (a, d, c) });
        }
        Pyramid(0); Pyramid(50);
        return new DesignShape { Kind = ShapeKind.Rectangle, X = 20, Y = 20, Width = 100, Height = 50, Model = Model3D.FromStl(Stl.Write(t), "twin", 9) };
    }

    private static (CncDocument Doc, CamReport Report) Chain(double stepover = 40)
    {
        var s = Twin();
        var doc = new CncDocument { Name = "Twin" };
        doc.Motion.StockTopMachineZ = -40;
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation
        {
            ShapeId = s.Id, Kind = CamOperationKind.Carve3D, ToolName = "6 mm", ToolKind = ToolKind.FlatEndMill, ToolDiameter = 6,
            TotalDepth = 9, DepthPerPass = 3, StepoverPercent = stepover, UseTabs = false, FeedRate = 1500, PlungeRate = 400,
            SecondToolEnabled = true, SecondToolName = "Konisk", SecondToolKind = ToolKind.TaperedBallNose, SecondToolDiameter = 0.8,
            SecondToolAngleDeg = 11, SecondDepthPerPass = 9, SecondStepoverMm = 0.5, SecondFeedRate = 1500, SecondPlungeRate = 400,
        });
        return (doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
    }

    private const string LinkPurpose = "Till nästa bana strax över materialet";

    [Fact]
    public void Roughing_crosses_just_above_the_material_instead_of_lifting_to_travel_height()
    {
        var (doc, report) = Chain();
        Assert.DoesNotContain(report.Operations.SelectMany(o => o.Findings), f => f.Severity == CamSeverity.Critical);
        var moves = report.Motion.Moves;
        int links = moves.Count(mv => mv.Purpose == LinkPurpose);
        int travelLifts = moves.Count(mv => mv.Purpose == "Lyft till förflyttningshöjd");
        Assert.True(links > 20, $"{links} short links");
        Assert.True(travelLifts <= 3, $"{travelLifts} lifts to travel height");
        // Links are G1 at the cutting feed, never rapids, and always below travel height.
        Assert.All(moves.Where(mv => mv.Purpose == LinkPurpose), mv =>
        {
            Assert.Equal(MoveKind.Feed, mv.Kind);
            Assert.True(mv.Z0 < doc.Motion.TravelHeightMm);
        });
    }

    /// <summary>
    /// Independent check: replay every cut with a flat 6 mm tool on a fine grid and verify that each short link
    /// passes above the material that is really left at that moment.
    /// </summary>
    [Theory]
    [InlineData(40)]
    [InlineData(90)]   // the widest allowed stepover: rows 5.4 mm apart with a 6 mm tool
    public void Every_short_link_clears_the_material_left_at_that_moment(double stepover)
    {
        var (doc, report) = Chain(stepover);
        var rough = report.Operations[0];
        var preview = rough.Preview!;
        const double cell = 0.25, r = 3.0;
        double minX = 10, minY = 10; int nx = (int)(120 / cell), ny = (int)(70 / cell);
        var h = new double[nx * ny];
        int reach = (int)Math.Ceiling(r / cell);
        void Stamp(double x, double y, double z)
        {
            int ci = (int)((x - minX) / cell), cj = (int)((y - minY) / cell);
            for (int j = cj - reach; j <= cj + reach; j++)
                for (int i = ci - reach; i <= ci + reach; i++)
                {
                    if (i < 0 || j < 0 || i >= nx || j >= ny) continue;
                    double cx = minX + (i + 0.5) * cell - x, cy = minY + (j + 0.5) * cell - y;
                    if (cx * cx + cy * cy > r * r) continue;
                    if (z < h[j * nx + i]) h[j * nx + i] = z;
                }
        }
        double HighestUnder(double x, double y)
        {
            double best = double.MinValue;
            int ci = (int)((x - minX) / cell), cj = (int)((y - minY) / cell);
            for (int j = cj - reach; j <= cj + reach; j++)
                for (int i = ci - reach; i <= ci + reach; i++)
                {
                    if (i < 0 || j < 0 || i >= nx || j >= ny) continue;
                    double cx = minX + (i + 0.5) * cell - x, cy = minY + (j + 0.5) * cell - y;
                    if (cx * cx + cy * cy > r * r) continue;
                    best = Math.Max(best, h[j * nx + i]);
                }
            return best;
        }
        int checkedLinks = 0;
        foreach (var mv in report.Motion.Moves.Where(mv => mv.OperationIndex == 0 || mv.Purpose == LinkPurpose))
        {
            if (mv.Purpose == LinkPurpose)
            {
                int n = Math.Max(1, (int)Math.Ceiling(mv.XYLength / 0.2));
                for (int t = 0; t <= n; t++)
                {
                    double x = mv.X0 + (mv.X1 - mv.X0) * t / n, y = mv.Y0 + (mv.Y1 - mv.Y0) * t / n;
                    double top = HighestUnder(x, y);
                    Assert.True(mv.Z0 >= top + 0.5, $"link at Z {mv.Z0:0.###} over material at {top:0.###} ({x:0.##}, {y:0.##})");
                }
                checkedLinks++;
                continue;
            }
            if (!mv.FollowsPath || mv.Kind != MoveKind.Feed) continue;
            var path = preview.Centerlines[mv.PathIndex]; var z = preview.PathZ[mv.PathIndex];
            for (int i = 1; i < path.Count; i++)
            {
                double len = Math.Sqrt(Math.Pow(path[i].X - path[i - 1].X, 2) + Math.Pow(path[i].Y - path[i - 1].Y, 2));
                int n = Math.Max(1, (int)Math.Ceiling(len / 0.2));
                for (int t = 0; t <= n; t++)
                    Stamp(path[i - 1].X + (path[i].X - path[i - 1].X) * t / n, path[i - 1].Y + (path[i].Y - path[i - 1].Y) * t / n, z[i - 1] + (z[i] - z[i - 1]) * t / n);
            }
        }
        Assert.True(checkedLinks > 20);
    }

    [Fact]
    public void The_whole_chain_still_passes_the_gcode_check_and_links_are_g1()
    {
        var (doc, report) = Chain();
        var (jobs, blocking) = GCodeGenerator.GenerateByTool(doc, report);
        Assert.Empty(blocking);
        Assert.All(jobs, j => Assert.True(j.Result.Success, string.Join("; ", j.Result.BlockingReasons.Concat(j.Result.VerifierIssues))));
    }

    [Fact]
    public void Run_time_counts_3d_paths_at_the_cutting_feed_along_their_3d_length()
    {
        var (doc, report) = Chain();
        var finish = report.Operations[1];
        var p = finish.Preview!;
        double length = 0;
        for (int k = 0; k < p.Centerlines.Count; k++)
            for (int i = 1; i < p.Centerlines[k].Count; i++)
            {
                double dx = p.Centerlines[k][i].X - p.Centerlines[k][i - 1].X, dy = p.Centerlines[k][i].Y - p.Centerlines[k][i - 1].Y, dz = p.PathZ[k][i] - p.PathZ[k][i - 1];
                length += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        var est = RunTimeEstimator.Estimate(doc, report);
        double expected = length / finish.Operation.FeedRate;
        // Plus the plunge into the first point; the old estimate was ~43 % longer (ramp feed).
        Assert.InRange(est.MinutesPerOperation[1], expected, expected + 0.2);
    }
}
