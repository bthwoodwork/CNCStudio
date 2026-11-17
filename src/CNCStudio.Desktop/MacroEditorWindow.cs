using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core.Grbl;
using Microsoft.Win32;

namespace CNCStudio.Desktop;

/// <summary>
/// Edits a macro: name, description and the code, with building blocks to insert (each says what it does),
/// a G-code file to read in, and Kontrollera that shows what would be refused before it is saved.
/// </summary>
public sealed class MacroEditorWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Good = B(0x7C, 0xD9, 0x9C), Bad = B(0xFF, 0x7A, 0x86);

    private readonly Macro _macro;
    private readonly TextBox _name = new() { Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 2, 0, 6) };
    private readonly TextBox _description = new() { Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(0, 2, 0, 6) };
    private readonly TextBox _code = new()
    {
        AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 13, Padding = new Thickness(6),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap
    };
    private readonly ComboBox _snippets = new() { MinWidth = 300, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _runAt = new() { Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 300 };
    private readonly TextBlock _check = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public Macro? Result { get; private set; }

    public MacroEditorWindow(Macro macro, bool isNew)
    {
        _macro = macro;
        Title = isNew ? "Nytt makro" : $"Ändra makro – {macro.Name}";
        Width = 760; Height = 640; MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");
        _name.Text = macro.Name; _description.Text = macro.Description; _code.Text = macro.Code;
        foreach (var t in Enum.GetValues<MacroTrigger>())
            _runAt.Items.Add(new ComboBoxItem { Content = t == MacroTrigger.None ? "Nej – bara när du kör det" : "Ja, " + Macro.TriggerText(t), Tag = t });
        _runAt.SelectedIndex = (int)macro.RunAt;

        foreach (var (code, what) in MacroCode.Snippets)
            _snippets.Items.Add(new ComboBoxItem { Content = $"{code}   –   {what}", Tag = code, ToolTip = what });
        _snippets.SelectedIndex = 0;
        var insert = Btn("Infoga", B(0x34, 0x47, 0x5A));
        insert.ToolTip = "Lägger in den valda raden där markören står";
        insert.Click += (_, _) => Insert();
        var file = Btn("Läs in G-code-fil…", B(0x34, 0x47, 0x5A));
        file.Click += (_, _) => ReadFile();
        var check = Btn("Kontrollera", B(0x34, 0x47, 0x5A));
        check.Click += (_, _) => Check();
        var save = Btn("Spara", B(0x24, 0x61, 0x8C)); save.IsDefault = false;
        save.Click += (_, _) => Save();
        var cancel = Btn("Avbryt", B(0x34, 0x47, 0x5A)); cancel.IsCancel = true;

        var top = new StackPanel { Children =
        {
            Label("Namn"), _name, Label("Beskrivning"), _description,
            Label("Körs automatiskt"), _runAt,
            new TextBlock { Text = "Ett makro per tillfälle – väljer du ett som ett annat makro har, tas det från det. Före varje program körs makrot innan programmet startar " +
                                   "(också från rad och vid återupptagning); stoppas makrot startar inte programmet. Efter Stoppa körs det när maskinen har stannat. " +
                                   "Vid paus går det inte: när maskinen står i paus tar GRBL inte emot G-code.",
                Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) },
            new TextBlock { Text = "KOD – en rad per kommando. Kommentarer i ( ) eller efter ; är tillåtna.", FontWeight = FontWeights.Bold, FontSize = 11, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 4, 0, 4) },
            new DockPanel { Margin = new Thickness(0, 0, 0, 6), Children = { _snippets, insert, file } },
        } };
        DockPanel.SetDock(insert, Dock.Right); DockPanel.SetDock(file, Dock.Right);
        var bottom = new StackPanel { Children =
        {
            _check,
            new TextBlock { Text = "Inställningar ($n=), $RST, startblock ($N) och $SLP stoppas – ändra inställningar i Maskininställningar, där en kopia sparas först. " +
                                   "Makron som kan röra maskinen frågar innan de körs. G53 och mjuka gränser stämmer först efter homing.",
                Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
            new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { check, save, cancel } },
        } };
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(_code);
        Content = root;
        Loaded += (_, _) => { (isNew ? _name : _code).Focus(); if (isNew) _name.SelectAll(); };
    }

    private void Insert()
    {
        if ((_snippets.SelectedItem as ComboBoxItem)?.Tag is not string code) return;
        int at = _code.CaretIndex;
        // On a line of its own: after the current line.
        int lineEnd = _code.Text.IndexOf('\n', at);
        int pos = lineEnd < 0 ? _code.Text.Length : lineEnd;
        string prefix = pos > 0 && _code.Text[pos - 1] != '\n' ? "\n" : "";
        _code.Text = _code.Text.Insert(pos, prefix + code);
        _code.CaretIndex = pos + prefix.Length + code.Length;
        _code.Focus();
    }

    private void ReadFile()
    {
        var dialog = new OpenFileDialog { Filter = "G-code (*.nc;*.gcode;*.ngc;*.tap;*.txt)|*.nc;*.gcode;*.ngc;*.tap;*.txt|Alla filer|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var text = MacroCode.FromFile(File.ReadAllText(dialog.FileName));
            _code.Text = _code.Text.Length == 0 ? text : _code.Text.TrimEnd() + "\n" + text;
            if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text == "Nytt makro") _name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            Check();
        }
        catch (Exception ex) { _check.Foreground = Bad; _check.Text = "Filen kunde inte läsas: " + ex.Message; }
    }

    /// <summary>Shows what would be sent or refused. True when it can be saved.</summary>
    private bool Check()
    {
        var p = MacroCode.Prepare(_code.Text);
        if (p.Ok)
        {
            _check.Foreground = Good;
            _check.Text = $"{p.Lines.Count} rader skickas." + (MacroCode.MovesMachine(p.Lines) ? " Makrot kan röra maskinen – du får en fråga innan det körs." : " Makrot rör inte maskinen.");
            return true;
        }
        _check.Foreground = Bad;
        _check.Text = string.Join("\n", p.Problems.Take(8)) + (p.Problems.Count > 8 ? $"\n… och {p.Problems.Count - 8} till." : "");
        return false;
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { _check.Foreground = Bad; _check.Text = "Ge makrot ett namn."; _name.Focus(); return; }
        if (!Check()) return;
        _macro.Name = _name.Text.Trim(); _macro.Description = _description.Text.Trim(); _macro.Code = _code.Text.TrimEnd();
        _macro.RunAt = (_runAt.SelectedItem as ComboBoxItem)?.Tag is MacroTrigger t ? t : MacroTrigger.None;
        Result = _macro;
        DialogResult = true;
    }

    private static TextBlock Label(string t) => new() { Text = t, Foreground = Muted, FontSize = 11 };

    private static Button Btn(string text, Brush background) => new()
    {
        Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0),
        Foreground = Brushes.White, Background = background, BorderBrush = B(0x50, 0x67, 0x83)
    };

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
