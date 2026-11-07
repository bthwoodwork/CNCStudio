using CNCStudio.Core.GCode;

namespace CNCStudio.Core.Tests;

/// <summary>A pass deeper than the cut (cutting data 3 mm/pass, cut 2 mm deep) is one pass, not an error.</summary>
public class PassDepthTests
{
    [Theory]
    [InlineData(CamOperationKind.Pocket)]
    [InlineData(CamOperationKind.Outside)]
    [InlineData(CamOperationKind.Drill)]
    public void A_pass_deeper_than_the_total_depth_gives_one_pass(CamOperationKind kind)
    {
        var doc = new CncDocument();
        doc.Motion.StockTopMachineZ = -40;
        var s = new DesignShape { Kind = ShapeKind.Ellipse, X = 50, Y = 50, Width = 60, Height = 40 };
        doc.Shapes.Add(s);
        var op = new CamOperation { ShapeId = s.Id, Kind = kind, ToolDiameter = 6, TotalDepth = 2, DepthPerPass = 3, UseTabs = false };
        doc.CamOperations.Add(op);
        Assert.Null(CamValidation.GetError(op, doc.MaterialThickness));

        var report = new CamPlanner(ContourToolpaths.Generate).Review(doc, computeAll: true);
        var result = Assert.Single(report.Operations);
        Assert.Equal(new[] { 2.0 }, result.Preview!.PassDepths);
        Assert.DoesNotContain(result.Findings, f => f.Severity == CamSeverity.Critical);
        var g = GCodeGenerator.Generate(doc, report);
        Assert.True(g.Success, string.Join("\n", g.BlockingReasons.Concat(g.VerifierIssues)));
        Assert.Equal(-2, g.Lines.Where(l => !l.StartsWith("(") && l.Contains('Z'))
            .Min(l => double.Parse(l[(l.IndexOf('Z') + 1)..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)), 6);
    }

    [Fact]
    public void Zero_or_negative_pass_depth_is_still_refused()
    {
        Assert.NotNull(CamValidation.GetError(new CamOperation { TotalDepth = 2, DepthPerPass = 0 }, 18));
        Assert.NotNull(CamValidation.GetError(new CamOperation { TotalDepth = 2, DepthPerPass = -1 }, 18));
    }
}
