using Clipper2Lib;

namespace CNCStudio.Core.Tests;

public class ImageTraceTests
{
    /// <summary>A white image with a black ring (outer radius 40, inner 20) and one black speck.</summary>
    private static (byte[] Gray, int W, int H) Ring()
    {
        int w = 120, h = 100;
        var g = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double d = Math.Sqrt((x + 0.5 - 50) * (x + 0.5 - 50) + (y + 0.5 - 50) * (y + 0.5 - 50));
                g[y * w + x] = (byte)(d <= 40 && d >= 20 ? 0 : 255);
            }
        g[5 * w + 110] = 0;   // a speck
        return (g, w, h);
    }

    [Fact]
    public void A_ring_becomes_an_outline_with_a_hole_and_specks_go()
    {
        var (g, w, h) = Ring();
        var paths = ImageTrace.Trace(g, w, h, new TraceOptions());
        Assert.Equal(2, paths.Count);                                    // outer edge and the hole; the speck is gone
        double area = Math.Abs(Clipper.Area(paths));
        Assert.InRange(area, Math.PI * (1600 - 400) * 0.97, Math.PI * (1600 - 400) * 1.03);
        // Smoothing removes the pixel steps: far fewer points than the stair-step outline would have.
        Assert.True(ImageTrace.PointCount(paths) < 200, ImageTrace.PointCount(paths).ToString());
    }

    [Fact]
    public void Invert_takes_the_light_parts()
    {
        var (g, w, h) = Ring();
        var paths = ImageTrace.Trace(g, w, h, new TraceOptions { Invert = true });
        // Light: the whole background with a ring-shaped hole, plus the disc in the middle.
        Assert.True(Math.Abs(Clipper.Area(paths)) > w * h * 0.5);
    }

    [Fact]
    public void To_shape_scales_to_the_width_and_pockets()
    {
        var (g, w, h) = Ring();
        var shape = ImageTrace.ToShape(ImageTrace.Trace(g, w, h, new TraceOptions()), 80, 10, 10);
        Assert.Equal(80, shape.Width, 6);
        Assert.Equal(80, shape.Height, 1);                               // the ring is round
        var op = new CamOperation { ShapeId = shape.Id, Kind = CamOperationKind.Pocket, ToolDiameter = 3, TotalDepth = 2, DepthPerPass = 2, UseTabs = false };
        Assert.NotEmpty(PocketToolpaths.Generate(shape, op, 10).Centerlines);
    }

    [Fact]
    public void Blank_image_has_nothing_to_trace()
    {
        var g = Enumerable.Repeat((byte)255, 50 * 50).ToArray();
        var paths = ImageTrace.Trace(g, 50, 50, new TraceOptions());
        Assert.Empty(paths);
        Assert.Throws<ArgumentException>(() => ImageTrace.ToShape(paths, 50, 0, 0));
    }
}
