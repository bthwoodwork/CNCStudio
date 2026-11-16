using CNCStudio.Core;
using Xunit;

public class RulerTicksTests
{
    [Fact]
    public void A_table_gets_a_number_every_100_mm_and_ticks_every_10()
    {
        Assert.Equal(100, RulerTicks.LabelStep(750));
        var t = RulerTicks.Between(0, 750, 100);
        Assert.Equal(76, t.Count);
        Assert.Equal(8, t.Count(x => x.Labelled));                    // 0, 100 … 700
        Assert.Contains(t, x => x.Value == 50 && x.Medium);
    }

    [Fact]
    public void A_small_material_gets_finer_numbers_and_negative_values_work()
    {
        double step = RulerTicks.LabelStep(60);
        Assert.Equal(10, step);
        var t = RulerTicks.Between(30, -30, step);                       // centre zero: −30 … 30
        Assert.Equal(-30, t[0].Value);
        Assert.Contains(t, x => x.Value == 0 && x.Labelled);
        Assert.Equal(2, RulerTicks.MinorStep(step));
    }
}
