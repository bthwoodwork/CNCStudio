using System;
using System.Collections.Generic;
using System.Linq;

namespace CNCStudio.Core;

/// <summary>What a keyboard shortcut does on the machine.</summary>
public enum KeyAction
{
    JogXMinus, JogXPlus, JogYMinus, JogYPlus, JogZMinus, JogZPlus,
    JogXMinusYPlus, JogXPlusYPlus, JogXMinusYMinus, JogXPlusYMinus,
    SpeedPrecise, SpeedNormal, SpeedRapid, NextSpeed,
    ZeroXY, ZeroZ, GoToXY0, Home,
    Stop, Pause, Resume,
}

/// <summary>A key with modifiers, written "Ctrl+Shift+PageUp". The key is the name WPF gives it (Left, PageUp, D1, A …).</summary>
public sealed record KeyChord(string Key, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    public override string ToString() => (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + Key;

    /// <summary>"Ctrl+Shift+PageUp" → the chord; null for empty or a lone modifier.</summary>
    public static KeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool ctrl = false, shift = false, alt = false;
        string? key = null;
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                default: if (key is not null) return null; key = p; break;
            }
        }
        return key is null ? null : new KeyChord(key, ctrl, shift, alt);
    }

    public bool SameAs(KeyChord? other) => other is not null && Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt &&
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
}

public sealed class KeyBindingEntry
{
    public KeyAction Action { get; set; }
    /// <summary>"Ctrl+Shift+PageUp", empty = no key.</summary>
    public string Keys { get; set; } = "";
}

/// <summary>
/// Jogga med tangentbordet: one key per direction, held for
/// continuous jog, plus keys for speed, zeroing, go to zero, homing, stop, pause and resume – every key
/// chosen by the user (Inställningar\tangentbord.json, shared like the other settings). The keys only act
/// in the Maskin workspace (unless turned off) and never while typing in a field.
/// </summary>
public sealed class KeyboardShortcuts
{
    public bool Enabled { get; set; } = true;
    /// <summary>Only in the Maskin workspace (F3), so the arrow keys keep their meaning elsewhere.</summary>
    public bool OnlyInMachineWorkspace { get; set; } = true;
    public List<KeyBindingEntry> Bindings { get; set; } = new();

    /// <summary>Arrows for X/Y, Page Up/Down for Z, 1–3 for the speeds, Esc stops.</summary>
    public static KeyboardShortcuts Defaults()
    {
        var k = new KeyboardShortcuts();
        foreach (KeyAction a in Enum.GetValues<KeyAction>()) k.Bindings.Add(new KeyBindingEntry { Action = a, Keys = DefaultKey(a) });
        return k;
    }

    private static string DefaultKey(KeyAction a) => a switch
    {
        KeyAction.JogXMinus => "Left", KeyAction.JogXPlus => "Right", KeyAction.JogYPlus => "Up", KeyAction.JogYMinus => "Down",
        KeyAction.JogZPlus => "PageUp", KeyAction.JogZMinus => "PageDown",
        KeyAction.SpeedPrecise => "D1", KeyAction.SpeedNormal => "D2", KeyAction.SpeedRapid => "D3",
        KeyAction.Stop => "Escape",
        _ => "",
    };

    /// <summary>Every action once, in order; missing ones (a file from an older version) get no key.</summary>
    public KeyboardShortcuts Normalized()
    {
        var list = new List<KeyBindingEntry>();
        foreach (KeyAction a in Enum.GetValues<KeyAction>())
        {
            var b = Bindings?.FirstOrDefault(x => x is not null && x.Action == a);
            var chord = KeyChord.Parse(b?.Keys);
            list.Add(new KeyBindingEntry { Action = a, Keys = chord?.ToString() ?? "" });
        }
        return new KeyboardShortcuts { Enabled = Enabled, OnlyInMachineWorkspace = OnlyInMachineWorkspace, Bindings = list };
    }

    public KeyChord? ChordFor(KeyAction a) => KeyChord.Parse(Bindings.FirstOrDefault(b => b.Action == a)?.Keys);

    public void Set(KeyAction a, KeyChord? chord)
    {
        var b = Bindings.FirstOrDefault(x => x.Action == a);
        if (b is null) Bindings.Add(b = new KeyBindingEntry { Action = a });
        b.Keys = chord?.ToString() ?? "";
    }

    /// <summary>The action bound to exactly this key and modifiers, if any.</summary>
    public KeyAction? Find(KeyChord pressed)
    {
        foreach (var b in Bindings)
            if (KeyChord.Parse(b.Keys) is { } c && c.SameAs(pressed)) return b.Action;
        return null;
    }

    /// <summary>Keys the rest of the program uses (F1–F4 for the workspaces, Ctrl+S, Ctrl+Z …) – not allowed here.</summary>
    public static readonly IReadOnlyList<KeyChord> Reserved = new[]
    {
        new KeyChord("F1"), new KeyChord("F2"), new KeyChord("F3"), new KeyChord("F4"),
        new KeyChord("S", Ctrl: true), new KeyChord("S", Ctrl: true, Shift: true), new KeyChord("E", Ctrl: true),
        new KeyChord("I", Ctrl: true), new KeyChord("O", Ctrl: true), new KeyChord("N", Ctrl: true),
        new KeyChord("A", Ctrl: true), new KeyChord("G", Ctrl: true), new KeyChord("G", Ctrl: true, Shift: true),
        new KeyChord("Z", Ctrl: true), new KeyChord("Y", Ctrl: true), new KeyChord("D", Ctrl: true),
        new KeyChord("F4", Alt: true), new KeyChord("Tab"), new KeyChord("Tab", Shift: true),
    };

    /// <summary>Two actions on one key, or a key the program already uses.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var seen = new List<(KeyChord Chord, KeyAction Action)>();
        foreach (var b in Bindings)
        {
            if (string.IsNullOrWhiteSpace(b.Keys)) continue;
            if (KeyChord.Parse(b.Keys) is not { } c) { problems.Add($"{ActionName(b.Action)}: \"{b.Keys}\" är ingen tangent."); continue; }
            if (Reserved.Any(r => r.SameAs(c))) problems.Add($"{ActionName(b.Action)}: {c} används redan av programmet.");
            if (seen.FirstOrDefault(s => s.Chord.SameAs(c)) is { Chord: not null } other)
                problems.Add($"{c} är vald för både {ActionName(other.Action)} och {ActionName(b.Action)}.");
            seen.Add((c, b.Action));
        }
        return problems;
    }

    /// <summary>The jog direction (as the jog buttons' tags, "X-", "X+Y-" …), or null for other actions.</summary>
    public static string? JogTag(KeyAction a) => a switch
    {
        KeyAction.JogXMinus => "X-", KeyAction.JogXPlus => "X+", KeyAction.JogYMinus => "Y-", KeyAction.JogYPlus => "Y+",
        KeyAction.JogZMinus => "Z-", KeyAction.JogZPlus => "Z+",
        KeyAction.JogXMinusYPlus => "X-Y+", KeyAction.JogXPlusYPlus => "X+Y+", KeyAction.JogXMinusYMinus => "X-Y-", KeyAction.JogXPlusYMinus => "X+Y-",
        _ => null,
    };

    public static string ActionName(KeyAction a) => a switch
    {
        KeyAction.JogXMinus => "Jogga X−", KeyAction.JogXPlus => "Jogga X+", KeyAction.JogYMinus => "Jogga Y−", KeyAction.JogYPlus => "Jogga Y+",
        KeyAction.JogZMinus => "Jogga Z− (ned)", KeyAction.JogZPlus => "Jogga Z+ (upp)",
        KeyAction.JogXMinusYPlus => "Jogga ↖ (X− Y+)", KeyAction.JogXPlusYPlus => "Jogga ↗ (X+ Y+)",
        KeyAction.JogXMinusYMinus => "Jogga ↙ (X− Y−)", KeyAction.JogXPlusYMinus => "Jogga ↘ (X+ Y−)",
        KeyAction.SpeedPrecise => "Hastighet Precis", KeyAction.SpeedNormal => "Hastighet Normal", KeyAction.SpeedRapid => "Hastighet Snabb",
        KeyAction.NextSpeed => "Nästa hastighet",
        KeyAction.ZeroXY => "Nolla X + Y", KeyAction.ZeroZ => "Nolla Z", KeyAction.GoToXY0 => "Gå till X0 Y0", KeyAction.Home => "Homing",
        KeyAction.Stop => "Stoppa joggen", KeyAction.Pause => "Paus (feed hold)", KeyAction.Resume => "Fortsätt (cycle start)",
        _ => a.ToString(),
    };

    /// <summary>Key names as people say them: "PageUp" → "Page Up", "D1" → "1", "Left" → "←".</summary>
    public static string Display(KeyChord? c)
    {
        if (c is null) return "–";
        string key = c.Key switch
        {
            "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓", "PageUp" => "Page Up", "PageDown" => "Page Down",
            "Escape" => "Esc", "Space" => "Mellanslag", "Return" or "Enter" => "Enter", "Back" => "Backsteg",
            "Next" => "Page Down", "Prior" => "Page Up", "Add" => "Num +", "Subtract" => "Num −",
            _ when c.Key.Length == 2 && c.Key[0] == 'D' && char.IsDigit(c.Key[1]) => c.Key[1..],
            _ when c.Key.StartsWith("NumPad", StringComparison.Ordinal) => "Num " + c.Key[6..],
            _ => c.Key,
        };
        return (c.Ctrl ? "Ctrl+" : "") + (c.Shift ? "Shift+" : "") + (c.Alt ? "Alt+" : "") + key;
    }
}
