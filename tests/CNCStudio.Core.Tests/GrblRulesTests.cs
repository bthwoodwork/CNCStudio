using CNCStudio.Core;

namespace CNCStudio.Core.Tests;

/// <summary>
/// Pins GRBL 1.1 behaviour verified against the GRBL source (see docs/GRBL-verifierat.md).
/// If one of these fails, re-check the source before changing the test.
/// </summary>
public class GrblRulesTests
{
    [Theory]
    // $23 = homing dir invert mask: X=1, Y=2, Z=4. Default 0 = every axis homes positive.
    // Corner assumes +X right and +Y towards the back.
    [InlineData(0, HomeCorner.BackRight)]
    [InlineData(1, HomeCorner.BackLeft)]
    [InlineData(2, HomeCorner.FrontRight)]
    [InlineData(3, HomeCorner.FrontLeft)]
    public void Home_corner_follows_the_23_mask(int mask, HomeCorner expected)
    {
        Assert.Equal(expected, new MachineProfile { HomingDirInvertMask = mask }.HomeCorner);
    }

    [Fact]
    public void Default_23_is_zero_all_axes_positive()
    {
        var m = new MachineProfile();
        Assert.Equal(0, m.HomingDirInvertMask);
        Assert.Equal(HomeCorner.BackRight, m.HomeCorner);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public void Z_homing_downwards_is_rejected(int mask)
    {
        var doc = new CncDocument();
        doc.Machine.HomingDirInvertMask = mask;
        Assert.Contains("Z", MachineSetupValidation.Validate(doc));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void Mask_outside_0_to_7_is_rejected(int mask)
    {
        var doc = new CncDocument();
        doc.Machine.HomingDirInvertMask = mask;
        Assert.NotNull(MachineSetupValidation.Validate(doc));
    }

    [Fact]
    public void Default_3_is_zero_no_axis_inverted()
    {
        Assert.Equal(0, new MachineProfile().DirInvertMask);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)] // X + Z
    [InlineData(7)] // all axes
    public void Any_3_mask_0_to_7_is_valid(int mask)
    {
        var doc = new CncDocument();
        doc.Machine.DirInvertMask = mask;
        Assert.Null(MachineSetupValidation.Validate(doc));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void Mask_3_outside_0_to_7_is_rejected(int mask)
    {
        var doc = new CncDocument();
        doc.Machine.DirInvertMask = mask;
        Assert.Contains("$3", MachineSetupValidation.Validate(doc));
    }

    [Fact]
    public void Inverting_3_does_not_change_the_home_corner_or_coordinates()
    {
        // $3 only flips the direction pin (stepper.c); $23 alone decides the homing direction in coordinates.
        var a = new MachineProfile { DirInvertMask = 0, HomingDirInvertMask = 1 };
        var b = new MachineProfile { DirInvertMask = 7, HomingDirInvertMask = 1 };
        Assert.Equal(a.HomeCorner, b.HomeCorner);
    }

    [Fact]
    public void Direction_and_homing_masks_survive_save_and_load()
    {
        var doc = new CncDocument();
        doc.Machine.DirInvertMask = 6; doc.Machine.HomingDirInvertMask = 3;
        var path = Path.GetTempFileName();
        try
        {
            DocumentStorage.Save(path, doc);
            var m = DocumentStorage.Load(path).Machine;
            Assert.Equal((6, 3), (m.DirInvertMask, m.HomingDirInvertMask));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Margin_smaller_than_pull_off_is_rejected()
    {
        // After $H a positive-homing axis sits at MPos = −$27; the switch is at 0.
        // A smaller margin would let planned moves come closer to the switch than homing stops.
        var doc = new CncDocument();
        doc.Machine.HomingPullOffMm = 2;
        doc.Machine.LimitMarginMm = 1.5;
        Assert.Contains("$27", MachineSetupValidation.Validate(doc));
        doc.Machine.LimitMarginMm = 2;
        Assert.Null(MachineSetupValidation.Validate(doc));
    }

    [Fact]
    public void Z_range_is_minus_132_to_zero()
    {
        // Soft limits (system.c): −max_travel ≤ MPos ≤ 0. Planned cut at MPos −62 with $132 = 65
        // and 3 mm margin is exactly on the allowed edge; 0.1 mm deeper is critical.
        var doc = new CncDocument();
        doc.Machine.ZTravelMm = 65; doc.Machine.LimitMarginMm = 3;
        var s = new DesignShape { Kind = ShapeKind.Rectangle, X = 100, Y = 100, Width = 50, Height = 50 };
        doc.Shapes.Add(s);
        doc.CamOperations.Add(new CamOperation { ShapeId = s.Id, TotalDepth = 6, DepthPerPass = 2 });
        doc.Motion.StockTopMachineZ = -56;   // deepest −62: allowed
        Assert.DoesNotContain(new CamPlanner(ContourToolpaths.Generate).Review(doc).GlobalFindings, f => f.Code == "ZBottomExceeded");
        doc.Motion.StockTopMachineZ = -56.1; // deepest −62.1: too deep
        Assert.Contains(new CamPlanner(ContourToolpaths.Generate).Review(doc).GlobalFindings, f => f.Code == "ZBottomExceeded");
    }
}
