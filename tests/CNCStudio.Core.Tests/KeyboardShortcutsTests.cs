using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CNCStudio.Core;
using Xunit;

namespace CNCStudio.Core.Tests;

public class KeyboardShortcutsTests
{
    [Fact]
    public void Chord_round_trips_and_ignores_case_and_order()
    {
        var c = KeyChord.Parse("shift+ctrl+PageUp");
        Assert.Equal(new KeyChord("PageUp", Ctrl: true, Shift: true), c);
        Assert.Equal("Ctrl+Shift+PageUp", c!.ToString());
        Assert.True(c.SameAs(KeyChord.Parse("Ctrl+Shift+pageup")));
        Assert.Null(KeyChord.Parse("Ctrl+Shift"));
        Assert.Null(KeyChord.Parse("A+B"));
        Assert.Null(KeyChord.Parse(""));
    }

    [Fact]
    public void Defaults_use_arrows_and_page_keys_and_are_valid()
    {
        var k = KeyboardShortcuts.Defaults();
        Assert.Equal(KeyAction.JogXMinus, k.Find(new KeyChord("Left")));
        Assert.Equal(KeyAction.JogYPlus, k.Find(new KeyChord("Up")));
        Assert.Equal(KeyAction.JogZMinus, k.Find(new KeyChord("PageDown")));
        Assert.Equal(KeyAction.Stop, k.Find(new KeyChord("Escape")));
        Assert.Null(k.Find(new KeyChord("Left", Shift: true)));     // modifiers must match exactly
        Assert.Empty(k.Validate());
        Assert.Equal(System.Enum.GetValues<KeyAction>().Length, k.Bindings.Count);
    }

    [Fact]
    public void Validate_finds_duplicates_and_reserved_keys()
    {
        var k = KeyboardShortcuts.Defaults();
        k.Set(KeyAction.ZeroXY, new KeyChord("Left"));
        k.Set(KeyAction.ZeroZ, new KeyChord("S", Ctrl: true));
        var p = k.Validate();
        Assert.Contains(p, x => x.Contains("både") && x.Contains("Nolla X + Y"));
        Assert.Contains(p, x => x.Contains("Ctrl+S") && x.Contains("används redan"));
    }

    [Fact]
    public void Normalized_adds_missing_actions_and_drops_bad_keys()
    {
        var k = new KeyboardShortcuts { Bindings = { new KeyBindingEntry { Action = KeyAction.ZeroZ, Keys = "ctrl+z+q" }, new KeyBindingEntry { Action = KeyAction.Home, Keys = "h" } } }.Normalized();
        Assert.Equal(System.Enum.GetValues<KeyAction>().Length, k.Bindings.Count);
        Assert.Null(k.ChordFor(KeyAction.ZeroZ));
        Assert.Equal(KeyAction.Home, k.Find(new KeyChord("H")));
        Assert.Null(k.ChordFor(KeyAction.JogXMinus));   // a saved file keeps the user's choice: no key
    }

    [Fact]
    public void Survives_json_and_maps_jog_directions()
    {
        var o = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var k = JsonSerializer.Deserialize<KeyboardShortcuts>(JsonSerializer.Serialize(KeyboardShortcuts.Defaults(), o), o)!.Normalized();
        Assert.Equal(KeyAction.JogZPlus, k.Find(new KeyChord("PageUp")));
        Assert.Equal("X+Y-", KeyboardShortcuts.JogTag(KeyAction.JogXPlusYMinus));
        Assert.Null(KeyboardShortcuts.JogTag(KeyAction.Home));
        Assert.All(System.Enum.GetValues<KeyAction>().Where(a => KeyboardShortcuts.JogTag(a) is not null),
            a => Assert.NotNull(CNCStudio.Core.Grbl.GrblMachineCommands.JogDirection(KeyboardShortcuts.JogTag(a)!)));
        Assert.Equal("Ctrl+Page Up", KeyboardShortcuts.Display(new KeyChord("PageUp", Ctrl: true)));
        Assert.Equal("1", KeyboardShortcuts.Display(new KeyChord("D1")));
    }
}
