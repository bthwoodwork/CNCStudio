using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>No tool changer: one program per tool, run in order with Z zeroed again between.</summary>
public class ToolJobTests
{
    private static (CncDocument Doc, CamReport Report) Doc(params (string? Tool, double Diameter, CamOperationKind Kind)[] ops)
    {
        var doc = new CncDocument { Name = "Låda" };
        doc.Motion.StockTopMachineZ = -40;
        int i = 0;
        foreach (var (tool, d, kind) in ops)
        {
            var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 40 + i * 130, Y = 60, Width = 100, Height = 60 };
            doc.Shapes.Add(s);
            doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, ToolName = tool, ToolDiameter = d, Kind = kind,
                TotalDepth = 3, DepthPerPass = 1.5, UseTabs = false });
            i++;
        }
        return (doc, new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true));
    }

    [Fact]
    public void One_tool_gives_one_program_without_tool_change_text()
    {
        var (doc, report) = Doc(("6 mm rak fräs", 6, CamOperationKind.Outside), ("6 mm rak fräs", 6, CamOperationKind.Inside));
        var (jobs, blocking) = GCodeGenerator.GenerateByTool(doc, report);
        Assert.Empty(blocking);
        var job = Assert.Single(jobs);
        Assert.True(job.Result.Success, string.Join("\n", job.Result.BlockingReasons.Concat(job.Result.VerifierIssues)));
        Assert.Equal(2, job.Operations.Count);
        Assert.DoesNotContain(job.Result.Lines, l => l.Contains("BYT VERKTYG"));
    }

    [Fact]
    public void Each_tool_is_its_own_verified_program_in_run_order()
    {
        // Outside contour with the 6 mm cutter, a pocket with the 3 mm one: pockets run before outside contours.
        var (doc, report) = Doc(("6 mm rak fräs", 6, CamOperationKind.Outside), ("3 mm rak fräs", 3, CamOperationKind.Pocket));
        var (jobs, blocking) = GCodeGenerator.GenerateByTool(doc, report);
        Assert.Empty(blocking);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(new[] { "3 mm rak fräs", "6 mm rak fräs" }, jobs.Select(j => j.ToolLabel));
        Assert.All(jobs, j => Assert.True(j.Result.Success, string.Join("\n", j.Result.BlockingReasons.Concat(j.Result.VerifierIssues))));
        Assert.All(jobs, j => Assert.Single(j.Operations));
        Assert.Contains(jobs[0].Result.Lines, l => l.Contains("PROGRAM 1 AV 2"));
        Assert.Contains(jobs[1].Result.Lines, l => l.Contains("BYT VERKTYG") && l.Contains("NOLLA Z"));
        // Every program starts by lifting Z alone and ends with M30.
        Assert.All(jobs, j => Assert.Equal("M30", j.Result.Lines[^1]));
    }

    [Fact]
    public void Same_diameter_but_another_cutter_is_another_program()
    {
        var (doc, report) = Doc(("6 mm rak fräs", 6, CamOperationKind.Outside), ("6 mm spiral upcut", 6, CamOperationKind.Outside));
        Assert.Equal(2, GCodeGenerator.GenerateByTool(doc, report).Jobs.Count);
    }

    [Fact]
    public void A_single_program_never_mixes_tools()
    {
        var (doc, report) = Doc(("6 mm rak fräs", 6, CamOperationKind.Outside), ("3 mm rak fräs", 3, CamOperationKind.Outside));
        var r = GCodeGenerator.Generate(doc, report);
        Assert.False(r.Success);
        Assert.Contains(r.BlockingReasons, b => b.Contains("verktygsväxlare"));
    }

    [Fact]
    public void Nothing_is_made_when_export_is_blocked()
    {
        var (doc, report) = Doc(("6 mm rak fräs", 6, CamOperationKind.Outside), ("3 mm rak fräs", 3, CamOperationKind.Outside));
        doc.Motion.StockTopMachineZ = null;
        var (jobs, blocking) = GCodeGenerator.GenerateByTool(doc, report);
        Assert.Empty(jobs);
        Assert.NotEmpty(blocking);
    }

    [Fact]
    public void No_program_uses_a_tool_change_code()
    {
        var (doc, report) = Doc(("6 mm rak fräs", 6, CamOperationKind.Outside), ("3 mm rak fräs", 3, CamOperationKind.Pocket));
        foreach (var j in GCodeGenerator.GenerateByTool(doc, report).Jobs)
            Assert.DoesNotContain(j.Result.Lines, l => !l.StartsWith("(") && (l.Contains("M6") || l.StartsWith("T")));
    }
}
