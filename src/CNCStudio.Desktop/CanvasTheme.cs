using System.Windows.Media;

namespace CNCStudio.Desktop;

/// <summary>
/// One dark, technical palette for the 2D design and machine views (CAM-software style).
/// All brushes are frozen so they can be shared across thousands of canvas elements.
/// </summary>
internal static class CanvasTheme
{
    private static SolidColorBrush B(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    // Material (stock) and grid
    public static readonly Brush Stock = B(255, 0x1B, 0x27, 0x34);
    public static readonly Brush StockEdge = B(255, 0x5D, 0x7A, 0x96);
    public static readonly Color GridColor = Color.FromRgb(0x7F, 0xA3, 0xC6);

    // Design shapes
    public static readonly Brush ShapeStroke = B(255, 0x9C, 0xC8, 0xF0);
    public static readonly Brush ShapeFill = B(28, 0x6F, 0xB2, 0xF0);
    public static readonly Brush SelectedStroke = B(255, 0x3D, 0xD6, 0xF5);
    public static readonly Brush SelectedFill = B(40, 0x3D, 0xD6, 0xF5);
    public static readonly Brush SelectionFrame = B(170, 0x3D, 0xD6, 0xF5);
    public static readonly Brush GripFill = B(255, 0x10, 0x1B, 0x27);
    public static readonly Brush DraftStroke = B(255, 0xFF, 0xB0, 0x3B);

    // Toolpaths
    public static readonly Brush ToolBand = B(46, 0x2E, 0xE6, 0x9A);
    public static readonly Brush ToolPath = B(255, 0x45, 0xF0, 0xA8);
    public static readonly Brush Arrow = B(230, 0x45, 0xF0, 0xA8);
    public static readonly Brush Tab = B(235, 0xFF, 0xC8, 0x3D);
    public static readonly Brush TabEdge = B(255, 0x10, 0x1B, 0x27);
    public static readonly Brush Rapid = B(170, 0x6F, 0xB6, 0xFF);
    public static readonly Brush RaisedRapid = B(220, 0xD9, 0x7C, 0xFF);
    public static readonly Brush Critical = B(255, 0xFF, 0x4D, 0x5E);
    public static readonly Brush CriticalGlow = B(90, 0xFF, 0x4D, 0x5E);
    public static readonly Brush Warning = B(255, 0xFF, 0xA5, 0x2E);
    public static readonly Brush WarningGlow = B(80, 0xFF, 0xA5, 0x2E);

    // Work zero (X red, Y green like most CAD/CAM axes)
    public static readonly Brush AxisX = B(255, 0xFF, 0x5A, 0x5A);
    public static readonly Brush AxisY = B(255, 0x4C, 0xE0, 0x7A);
    public static readonly Brush ZeroRing = B(255, 0xE8, 0xF1, 0xFA);

    // Machine view
    public static readonly Brush MachineBed = B(255, 0x15, 0x20, 0x2C);
    public static readonly Brush MachineBedEdge = B(255, 0x46, 0x5D, 0x75);
    public static readonly Brush MachineGrid = B(40, 0x7F, 0xA3, 0xC6);
    public static readonly Brush MachineStock = B(255, 0x24, 0x33, 0x44);
    public static readonly Brush Fixture = B(150, 0xC9, 0x6A, 0x3A);
    public static readonly Brush FixtureEdge = B(255, 0xE0, 0x86, 0x52);
    public static readonly Brush FixtureSelected = B(255, 0xFF, 0xD8, 0x4D);
}
