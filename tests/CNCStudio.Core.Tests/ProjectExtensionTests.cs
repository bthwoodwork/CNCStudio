using CNCStudio.Core;
using Xunit;

public class ProjectExtensionTests
{
    [Theory]
    [InlineData(@"C:\P\Bricka", @"C:\P\Bricka.cncstudio.json")]
    [InlineData(@"C:\P\Bricka.json", @"C:\P\Bricka.cncstudio.json")]
    [InlineData(@"C:\P\Bricka.cncstudio.json", @"C:\P\Bricka.cncstudio.json")]
    [InlineData(@"C:\P\Bricka.CNCSTUDIO.JSON", @"C:\P\Bricka.CNCSTUDIO.JSON")]
    [InlineData(@"C:\P\Hylla v1.2", @"C:\P\Hylla v1.2.cncstudio.json")]
    public void A_saved_project_always_gets_the_project_extension(string typed, string saved)
        => Assert.Equal(saved, ProjectCatalog.WithExtension(typed));
}
