namespace CNCStudio.Core;

/// <summary>
/// Which point of the material its table position is given for: one of nine (3 × 3) – a corner, the middle of
/// an edge or the centre. <paramref name="Column"/> 0 = left, 1 = middle, 2 = right; <paramref name="Row"/>
/// 0 = back, 1 = middle, 2 = front (as the material is seen from above in 2D: back at the top).
/// </summary>
public readonly record struct StockAnchor(int Column, int Row)
{
    /// <summary>The material's corner nearest the home corner – where table distances are counted from.</summary>
    public static StockAnchor NearestHome(HomeCorner home) => new(
        home is HomeCorner.BackLeft or HomeCorner.FrontLeft ? 0 : 2,
        home is HomeCorner.FrontLeft or HomeCorner.FrontRight ? 2 : 0);

    private static bool Left(HomeCorner c) => c is HomeCorner.BackLeft or HomeCorner.FrontLeft;
    private static bool Front(HomeCorner c) => c is HomeCorner.FrontLeft or HomeCorner.FrontRight;

    /// <summary>
    /// The anchor's distance from the home corner along X and Y, for material whose back-left corner is at
    /// (<paramref name="stockX"/>, <paramref name="stockY"/>) on the table canvas (origin back-left, Y towards the front).
    /// </summary>
    public (double X, double Y) ToTable(MachineProfile m, double stockX, double stockY, double width, double length)
    {
        double px = stockX + Column * width / 2, py = stockY + Row * length / 2;
        return (Left(m.HomeCorner) ? px : m.WorkWidthMm - px, Front(m.HomeCorner) ? m.WorkLengthMm - py : py);
    }

    /// <summary>Inverse of <see cref="ToTable"/>: where the material's back-left corner goes for the anchor to land at (x, y).</summary>
    public (double StockX, double StockY) FromTable(MachineProfile m, double x, double y, double width, double length)
    {
        double px = Left(m.HomeCorner) ? x : m.WorkWidthMm - x, py = Front(m.HomeCorner) ? m.WorkLengthMm - y : y;
        return (px - Column * width / 2, py - Row * length / 2);
    }

    /// <summary>The anchor in the material's own frame (2D design canvas: origin back-left, Y towards the front).</summary>
    public (double X, double Y) OnMaterial(double width, double length) => (Column * width / 2, Row * length / 2);

    public string Name => (Column, Row) switch
    {
        (1, 1) => "mitten",
        _ => (Row switch { 0 => "bakre", 1 => "", _ => "främre" }) + (Row != 1 && Column != 1 ? " " : "") +
             (Column switch { 0 => "vänstra", 1 => "", _ => "högra" }) + (Column == 1 || Row == 1 ? " kanten" : " hörnet")
    };
}
