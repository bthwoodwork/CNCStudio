namespace CNCStudio.Core.Tests;

/// <summary>0.9.5: tool numbers, purchase details, measurements and categories that can be added, renamed and removed.</summary>
public class ToolLibraryDetailsTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "cncstudio-tools-" + Guid.NewGuid().ToString("N"), "verktyg.json");

    private static ToolDefinition Tool(string name, string category = "", double d = 6, int? number = null) =>
        new() { Name = name, Category = category, Diameter = d, ToolNumber = number };

    [Fact]
    public void Number_purchase_and_measurements_are_saved_and_read_back()
    {
        var path = TempFile();
        var t = Tool("6 mm rak fräs", number: 3);
        t.Vendor = "CNC-butiken"; t.ArticleNumber = "RF-6-22"; t.ProductLink = "https://example.com/rf6"; t.CutterMaterial = "Hårdmetall";
        t.ShankDiameter = 6; t.OverallLength = 50; t.FluteLength = 22; t.StickoutLength = 30;
        new ToolLibrary { Tools = { t }, Categories = { "Raka fräsar" } }.Save(path);

        var back = ToolLibrary.Load(path);
        var b = Assert.Single(back.Tools);
        Assert.Equal((3, "CNC-butiken", "RF-6-22", "https://example.com/rf6", "Hårdmetall"), (b.ToolNumber!.Value, b.Vendor, b.ArticleNumber, b.ProductLink, b.CutterMaterial));
        Assert.Equal((6.0, 50.0, 22.0, 30.0), (b.ShankDiameter!.Value, b.OverallLength!.Value, b.FluteLength!.Value, b.StickoutLength!.Value));
        Assert.Equal(new[] { "Raka fräsar" }, back.Categories);
        Assert.Equal("Raka fräsar: T3 6 mm rak fräs", b.DisplayName);
        Assert.Equal("T3  6 mm rak fräs", b.ListName);
    }

    [Fact]
    public void An_old_library_without_the_new_fields_loads_with_them_empty()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Tools": [ { "Name": "1/8\" rak", "Kind": "FlatEndMill", "Diameter": 3.175, "Flutes": 2, "FeedRate": 1000, "PlungeRate": 300, "DepthPerPass": 1.5, "SpindleRpm": 18000 } ] }""");
        var lib = ToolLibrary.Load(path);
        var t = Assert.Single(lib.Tools);
        Assert.Null(t.ToolNumber); Assert.Null(t.ShankDiameter); Assert.Equal("", t.Vendor);
        Assert.Empty(lib.Categories);
        Assert.Equal("Raka fräsar: 1/8\" rak", t.DisplayName);
    }

    [Theory]
    [InlineData(-1, null, null, null, "Verktygsnumret")]
    [InlineData(null, 0.0, null, null, "Skaftdiametern")]
    [InlineData(null, null, 40.0, 50.0, "Skärlängden kan inte")]
    public void Wrong_measurements_are_refused(int? number, double? shank, double? total, double? flute, string expected)
    {
        var t = Tool("X", number: number);
        t.ShankDiameter = shank; t.OverallLength = total; t.FluteLength = flute;
        Assert.StartsWith(expected, t.Validate());
    }

    [Fact]
    public void Flutes_longer_than_the_stickout_is_only_a_warning()
    {
        var t = Tool("X");
        t.FluteLength = 25; t.StickoutLength = 20;
        Assert.Null(t.Validate());
        Assert.Contains("spännhylsan", t.Warning);
        t.StickoutLength = 30;
        Assert.Null(t.Warning);
    }

    [Fact]
    public void V_bit_tip_must_be_smaller_than_the_diameter()
    {
        var t = new ToolDefinition { Name = "V", Kind = ToolKind.VBit, Diameter = 12.7, TipDiameter = 0 };
        Assert.Null(t.Validate());
        t.TipDiameter = 12.7;
        Assert.NotNull(t.Validate());
    }

    [Fact]
    public void Categories_come_in_the_users_order_then_the_others_alphabetically()
    {
        var lib = new ToolLibrary
        {
            Tools = { Tool("a", "Planfräsar"), Tool("b"), new ToolDefinition { Name = "v", Kind = ToolKind.VBit } },
            Categories = { "Kompressionsfräsar", "Planfräsar" }
        };
        Assert.Equal(new[] { "Kompressionsfräsar", "Planfräsar", "Raka fräsar", "V-fräsar" }, lib.AllCategories());
        Assert.True(lib.CategoryOrder("Planfräsar") < lib.CategoryOrder("Raka fräsar"));
    }

    [Fact]
    public void A_new_category_is_added_last_and_duplicates_are_refused()
    {
        var lib = new ToolLibrary { Tools = { Tool("a", "Raka fräsar") } };
        Assert.Null(lib.AddCategory("Gravyrfräsar"));
        Assert.Equal(new[] { "Raka fräsar", "Gravyrfräsar" }, lib.AllCategories());
        Assert.NotNull(lib.AddCategory("gravyrfräsar"));
        Assert.NotNull(lib.AddCategory("  "));
    }

    [Fact]
    public void Renaming_a_category_moves_its_tools_including_default_ones()
    {
        var lib = new ToolLibrary { Tools = { Tool("a"), Tool("b", "Raka fräsar"), Tool("c", "Planfräsar") } };
        Assert.Null(lib.RenameCategory("Raka fräsar", "Pinnfräsar"));
        Assert.All(lib.Tools.Take(2), t => Assert.Equal("Pinnfräsar", t.CategoryOrDefault));
        Assert.Equal("Planfräsar", lib.Tools[2].CategoryOrDefault);
        Assert.NotNull(lib.RenameCategory("Pinnfräsar", "Planfräsar"));   // already exists
        Assert.NotNull(lib.RenameCategory("Finns inte", "X"));
    }

    [Fact]
    public void Removing_a_category_with_tools_needs_somewhere_to_move_them()
    {
        var lib = new ToolLibrary { Tools = { Tool("a", "Gamla"), Tool("b", "Raka fräsar") }, Categories = { "Gamla", "Tom" } };
        Assert.NotNull(lib.DeleteCategory("Gamla", null));
        Assert.NotNull(lib.DeleteCategory("Gamla", "Gamla"));
        Assert.Null(lib.DeleteCategory("Gamla", "Övriga"));
        Assert.Equal("Övriga", lib.Tools[0].CategoryOrDefault);
        Assert.DoesNotContain("Gamla", lib.AllCategories());
        Assert.Contains("Övriga", lib.AllCategories());
        Assert.Null(lib.DeleteCategory("Tom", null));                       // an empty one goes without asking where
        Assert.DoesNotContain("Tom", lib.AllCategories());
    }

    [Fact]
    public void Duplicate_numbers_are_found_and_the_next_free_number_is_suggested()
    {
        var lib = new ToolLibrary { Tools = { Tool("a", number: 1), Tool("b", number: 2), Tool("c", number: 2), Tool("d", number: 4) } };
        var dup = Assert.Single(lib.DuplicateToolNumbers());
        Assert.Equal(2, dup.Number);
        Assert.Equal(new[] { "b", "c" }, dup.Tools);
        Assert.Equal(3, lib.NextFreeToolNumber());
        Assert.Equal(1, new ToolLibrary().NextFreeToolNumber());
    }

    [Fact]
    public void Clone_does_not_share_tools_or_categories()
    {
        var lib = new ToolLibrary { Tools = { Tool("a") }, Categories = { "X" } };
        var copy = lib.Clone();
        copy.Tools[0].Name = "b"; copy.Categories.Add("Y");
        Assert.Equal("a", lib.Tools[0].Name);
        Assert.Single(lib.Categories);
    }
}
