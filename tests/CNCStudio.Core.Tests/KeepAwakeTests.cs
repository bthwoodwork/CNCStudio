namespace CNCStudio.Core.Tests;

public class KeepAwakeTests
{
    [Theory]
    [InlineData(false, false, AwakeLevel.None)]
    [InlineData(true, false, AwakeLevel.System)]
    [InlineData(true, true, AwakeLevel.SystemAndDisplay)]
    public void The_level_follows_connection_and_running(bool connected, bool running, AwakeLevel level) =>
        Assert.Equal(level, KeepAwake.For(connected, running));

    [Fact]
    public void Flags_are_the_Windows_values_and_none_releases()
    {
        Assert.Equal(0x80000000u, KeepAwake.Flags(AwakeLevel.None));                 // ES_CONTINUOUS alone clears the request
        Assert.Equal(0x80000001u, KeepAwake.Flags(AwakeLevel.System));               // + ES_SYSTEM_REQUIRED
        Assert.Equal(0x80000003u, KeepAwake.Flags(AwakeLevel.SystemAndDisplay));     // + ES_DISPLAY_REQUIRED
    }
}
