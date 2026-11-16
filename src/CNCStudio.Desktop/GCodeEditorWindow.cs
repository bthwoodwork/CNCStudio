using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CNCStudio.Core.GCode;
using CNCStudio.Core.Grbl;
using Microsoft.Win32;

namespace CNCStudio.Desktop;

/// <summary>
/// G-code-editorn (Arkiv / Program → G-code-editor…): open a G-code file – from Fusion, VCarve or CNC Studio itself –
/// edit it as text, and mirror, rotate, move or copy it in a grid. Every change is checked the way Öppna G-code
/// checks a file, and the result is saved or loaded straight for a run (with the same checks before Starta).
/// </summary>
public sealed class GCodeEditorWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x8C, 0xA3, 0xBA), Card = B(0x1C, 0x2A, 0x39),
        Bad = B(0xFF, 0x7A, 0x86), Good = B(0x7C, 0xD9, 0x9C), Field = B(0x10, 0x20, 0x2E), Edge = B(0x3C, 0x51, 0x67);

    private readonly TextBox _editor = new()
    {
        FontFamily = new FontFamily("Consolas"), FontSize = 13, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = B(0x0E, 0x1A, 0x26), Foreground = Ink, CaretBrush = Ink, BorderBrush = Edge, Padding = new Thickness(6, 4, 6, 4)
    };
    private readonly TextBlock _info = new() { Foreground = Ink, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
    private readonly TextBlock _problems = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
    private readonly TextBlock _status = new() { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _pivot = new() { Margin = new Thickness(0, 2, 0, 6), SelectedIndex = 0 };
    private readonly TextBox _dx = Num("0"), _dy = Num("0"), _cols = Num("2"), _rows = Num("1"), _stepX = Num("0"), _stepY = Num("0");
    private readonly Button _undo = new() { Content = "Ångra ändring", IsEnabled = false, ToolTip = "Ta tillbaka den senaste speglingen, rotationen, flytten eller kopieringen" };
    private readonly Stack<string> _history = new();
    private readonly Func<string, string, string?, bool> _loadForRun;
    private readonly double _rapid;
    private string? _path;
    private bool _dirty, _loading;
    private GCodeFileInfo? _fileInfo;

    /// <param name="loadForRun">Loads the text as the program to run (text, name, file path or null); true when loaded.</param>
    public GCodeEditorWindow(string? path, Func<string, string, string?, bool> loadForRun, double rapidMmPerMin)
    {
        _loadForRun = loadForRun; _rapid = rapidMmPerMin;
        Width = 1180; Height = 780; MinWidth = 820; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        // ---- Toolbar ----
        var open = Btn("Öppna…", "", (_, _) => OpenFile());
        var save = Btn("Spara", "", (_, _) => Save(false));
        var saveAs = Btn("Spara som…", "", (_, _) => Save(true));
        var run = Btn("Ladda för körning", "", (_, _) => LoadForRun());
        run.FontWeight = FontWeights.SemiBold;
        run.ToolTip = "Ladda texten som det program som ska köras (Maskin → Program). Samma kontroller som Öppna G-code-fil, och Starta kontrollerar banan mot maskinen.";
        _undo.Click += (_, _) => UndoEdit();
        Ui.SetIcon(_undo, "");
        var bar = new WrapPanel { Margin = new Thickness(14, 10, 14, 6) };
        foreach (var b in new UIElement[] { open, save, saveAs, run, _undo }) bar.Children.Add(b);

        // ---- Tools ----
        _pivot.Items.Add(new ComboBoxItem { Content = "Kring banans mitt (stannar på samma ställe)" });
        _pivot.Items.Add(new ComboBoxItem { Content = "Kring nollpunkten X0 Y0" });
        var tools = new StackPanel { Width = 330, Margin = new Thickness(0, 0, 14, 0) };
        tools.Children.Add(CardOf("PROGRAMMET", new StackPanel { Children = { _info, _problems } }));

        var mirrorX = Btn("Spegla vänster–höger", "", (_, _) => Transform("Speglat vänster–höger", e => XYMap.MirrorX(Pivot(e).X)));
        var mirrorY = Btn("Spegla fram–bak", "", (_, _) => Transform("Speglat fram–bak", e => XYMap.MirrorY(Pivot(e).Y)));
        var ccw = Btn("Rotera 90° moturs", "", (_, _) => Transform("Roterat 90° moturs", e => XYMap.Rotate(90, Pivot(e).X, Pivot(e).Y)));
        var cw = Btn("Rotera 90° medurs", "", (_, _) => Transform("Roterat 90° medurs", e => XYMap.Rotate(-90, Pivot(e).X, Pivot(e).Y)));
        var half = Btn("Rotera 180°", "", (_, _) => Transform("Roterat 180°", e => XYMap.Rotate(180, Pivot(e).X, Pivot(e).Y)));
        var turn = new StackPanel();
        turn.Children.Add(new TextBlock { Text = "Spegla och rotera", Foreground = Muted, FontSize = 11 });
        turn.Children.Add(_pivot);
        turn.Children.Add(new UniformGrid { Columns = 2, Children = { mirrorX, mirrorY, ccw, cw, half } });
        tools.Children.Add(CardOf("SPEGLA OCH ROTERA", turn));

        var move = Btn("Flytta", "", (_, _) =>
        {
            if (!Read(_dx, "X", out var dx) || !Read(_dy, "Y", out var dy)) return;
            Transform($"Flyttat X {GCodeTransform.N(dx)} Y {GCodeTransform.N(dy)} mm", _ => XYMap.Translate(dx, dy));
        });
        var toZero = Btn("Till nollpunkten", "", (_, _) => Transform("Flyttat så att banans främre vänstra hörn ligger på X0 Y0",
            e => XYMap.Translate(-e.MinX, -e.MinY)));
        toZero.ToolTip = "Flytta banan så att dess främre vänstra hörn (minsta X och Y) hamnar på X0 Y0";
        var moveGrid = new Grid();
        moveGrid.ColumnDefinitions.Add(new ColumnDefinition()); moveGrid.ColumnDefinitions.Add(new ColumnDefinition());
        AddCell(moveGrid, Labeled("X (mm)", _dx), 0); AddCell(moveGrid, Labeled("Y (mm)", _dy), 1);
        tools.Children.Add(CardOf("FLYTTA", new StackPanel { Children = { moveGrid, new UniformGrid { Columns = 2, Children = { move, toZero } } } }));

        var copies = Btn("Skapa kopior", "", (_, _) => MakeGrid());
        copies.ToolTip = "Hela programmet upprepas i ett rutnät. Mellan kopiorna lyfts fräsen till programmets högsta Z innan den flyttar.";
        var grid = new Grid();
        for (int c = 0; c < 2; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        AddCell(grid, Labeled("Kolumner (X)", _cols), 0); AddCell(grid, Labeled("Rader (Y)", _rows), 1);
        AddCell(grid, Labeled("Avstånd X (mm)", _stepX), 0, 1); AddCell(grid, Labeled("Avstånd Y (mm)", _stepY), 1, 1);
        tools.Children.Add(CardOf("KOPIOR I RUTNÄT", new StackPanel
        {
            Children =
            {
                grid, copies,
                new TextBlock { Text = "Avståndet räknas från kopia till kopia (banans bredd/djup + mellanrum). Förslaget är banans storlek + 10 mm.",
                    Foreground = Muted, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 4, 3, 0) }
            }
        }));
        tools.Children.Add(new TextBlock
        {
            Text = "Bara X och Y ändras – Z, matning och varvtal står kvar. Bågar (G2/G3) följer med; spegling byter riktning på dem. " +
                   "Program i tum och rader med G10/G28/G30/G92 ändras inte.",
            Foreground = Muted, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 6, 3, 0)
        });

        // ---- Layout ----
        var close = new Button { Content = "Stäng", IsCancel = true, MinWidth = 90 };
        close.Click += (_, _) => Close();
        var bottom = new DockPanel { Margin = new Thickness(14, 6, 14, 12) };
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(close); bottom.Children.Add(_status);

        var body = new DockPanel { Margin = new Thickness(14, 0, 14, 0) };
        var toolScroll = new ScrollViewer { Content = tools, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(toolScroll, Dock.Right);
        body.Children.Add(toolScroll);
        body.Children.Add(_editor);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bar); root.Children.Add(bottom); root.Children.Add(body);
        Content = root;

        _editor.TextChanged += (_, _) => { if (_loading) return; _dirty = true; UpdateTitle(); ScheduleCheck(); };
        _editor.SelectionChanged += (_, _) => ShowCaret();
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Save(false)), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenFile), Key.O, ModifierKeys.Control));
        Closing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };

        if (path is not null) Load(path); else { SetText("", null); }
    }

    // ---- File ----

    private void OpenFile()
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog
        {
            Title = "Öppna G-code",
            Filter = "G-code (*.nc;*.gcode;*.ngc;*.tap;*.cnc;*.gc;*.txt)|*.nc;*.gcode;*.ngc;*.tap;*.cnc;*.gc;*.txt|Alla filer|*.*",
            InitialDirectory = _path is not null ? Path.GetDirectoryName(_path) : null
        };
        if (dialog.ShowDialog(this) == true) Load(dialog.FileName);
    }

    private void Load(string path)
    {
        try { SetText(File.ReadAllText(path), path); _history.Clear(); _undo.IsEnabled = false; _status.Text = "Öppnad: " + path; }
        catch (Exception ex) { _problems.Text = "Filen kunde inte läsas: " + ex.Message; }
    }

    private void SetText(string text, string? path)
    {
        _loading = true;
        _editor.Text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);
        _loading = false;
        _path = path; _dirty = false;
        UpdateTitle(); Check();
    }

    private bool Save(bool choose)
    {
        string? target = _path;
        if (choose || target is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Spara G-code", Filter = "G-code för GRBL (*.nc)|*.nc|G-code (*.gcode)|*.gcode|Alla filer|*.*",
                FileName = _path is null ? "program.nc" : Path.GetFileName(_path),
                InitialDirectory = _path is not null ? Path.GetDirectoryName(_path) : null
            };
            if (dialog.ShowDialog(this) != true) return false;
            target = dialog.FileName;
        }
        try
        {
            // ASCII with CRLF like CNC Studio's own programs; any other character would be refused by GRBL anyway.
            var text = string.Join("\r\n", Lines()) + "\r\n";
            if (text.Any(c => c > 126 && c != '\r' && c != '\n'))
            { _problems.Text = "Texten innehåller tecken utanför ASCII (t.ex. å, ä, ö) – GRBL förstår dem inte. Ta bort dem innan du sparar."; return false; }
            File.WriteAllText(target!, text, Encoding.ASCII);
            _path = target; _dirty = false; UpdateTitle();
            _status.Text = "Sparad: " + target;
            return true;
        }
        catch (Exception ex) { _problems.Text = "Kunde inte spara: " + ex.Message; return false; }
    }

    private void LoadForRun()
    {
        string name = _path is null ? "Redigerat program" : Path.GetFileName(_path) + (_dirty ? " (ändrad, ej sparad)" : "");
        if (_loadForRun(string.Join("\n", Lines()), name, _dirty ? null : _path))
            _status.Text = "Laddat för körning: " + name + ". Kör det under Maskin → Program.";
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var answer = MessageBox.Show(this, "Programmet är ändrat men inte sparat. Spara ändringarna?", "G-code-editor",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && Save(false));
    }

    private void UpdateTitle() =>
        Title = "G-code-editor – " + (_path is null ? "nytt program" : Path.GetFileName(_path)) + (_dirty ? " *" : "");

    // ---- Checking ----

    private System.Windows.Threading.DispatcherTimer? _checkTimer;

    /// <summary>Typing is checked a moment after it stops, so a large file does not slow the editor down.</summary>
    private void ScheduleCheck()
    {
        _checkTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _checkTimer.Tick -= CheckTick; _checkTimer.Tick += CheckTick;
        _checkTimer.Stop(); _checkTimer.Start();
    }

    private void CheckTick(object? sender, EventArgs e) { _checkTimer?.Stop(); Check(); }

    private IReadOnlyList<string> Lines() =>
        _editor.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');

    private void Check()
    {
        var text = _editor.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _fileInfo = null;
            _info.Text = "Tomt. Öppna en fil (Ctrl+O) eller skriv/klistra in G-code.";
            _problems.Text = "";
            return;
        }
        var program = ExternalGCode.Analyze(text, _path is null ? "program" : Path.GetFileName(_path), _rapid);
        _fileInfo = program.Info;
        _info.Text = program.Info is { } i ? ExternalGCode.Summary(i) : "";
        _problems.Text = program.Problems.Count == 0
            ? (program.Warnings.Count > 0 ? "OBS: " + string.Join(" ", program.Warnings) : "")
            : $"Kan inte köras med GRBL ({program.Problems.Count}):\n" + string.Join("\n", program.Problems.Take(8).Select(p => "• " + p)) +
              (program.Problems.Count > 8 ? $"\n• … och {program.Problems.Count - 8} till." : "");
        _problems.Foreground = program.Problems.Count == 0 ? Muted : Bad;
        if (program.Info is { } info && _stepX.Text == "0" && _stepY.Text == "0")
        {
            _stepX.Text = GCodeTransform.N(Math.Round(info.MaxX - info.MinX + 10, 1));
            _stepY.Text = GCodeTransform.N(Math.Round(info.MaxY - info.MinY + 10, 1));
        }
    }

    private void ShowCaret()
    {
        int index = _editor.CaretIndex;
        int line = _editor.GetLineIndexFromCharacterIndex(index);
        if (line < 0) return;
        int col = index - _editor.GetCharacterIndexFromLineIndex(line);
        if (_status.Text.StartsWith("Rad ", StringComparison.Ordinal) || _status.Text.Length == 0)
            _status.Text = $"Rad {line + 1}, kolumn {col + 1} av {_editor.LineCount} rader";
    }

    // ---- Edits ----

    private (double X, double Y) Pivot(GCodeFileInfo e) =>
        _pivot.SelectedIndex == 1 ? (0, 0) : ((e.MinX + e.MaxX) / 2, (e.MinY + e.MaxY) / 2);

    private void Transform(string what, Func<GCodeFileInfo, XYMap> mapFor)
    {
        Check();
        if (_fileInfo is not { } info) { _problems.Foreground = Bad; _problems.Text = "Det finns inga rörelser att ändra – eller programmet kan inte läsas (se ovan)."; return; }
        var result = GCodeTransform.Apply(Lines(), mapFor(info));
        Replace(what, result);
    }

    private void MakeGrid()
    {
        Check();
        if (_fileInfo is not { } info) { _problems.Foreground = Bad; _problems.Text = "Det finns inga rörelser att kopiera."; return; }
        if (!int.TryParse(_cols.Text.Trim(), out var cols) || !int.TryParse(_rows.Text.Trim(), out var rows))
        { _problems.Foreground = Bad; _problems.Text = "Ange antal kolumner och rader som heltal."; return; }
        if (!Read(_stepX, "avstånd X", out var sx) || !Read(_stepY, "avstånd Y", out var sy)) return;
        if (info.MaxZ <= 0)
        { _problems.Foreground = Bad; _problems.Text = "Programmet lyfter aldrig över Z0 – det finns ingen säker höjd att flytta mellan kopiorna på."; return; }
        var width = info.MaxX - info.MinX; var depth = info.MaxY - info.MinY;
        if ((cols > 1 && Math.Abs(sx) < width) || (rows > 1 && Math.Abs(sy) < depth))
        {
            if (MessageBox.Show(this, $"Avståndet är mindre än banans storlek ({GCodeTransform.N(width)} × {GCodeTransform.N(depth)} mm) – kopiorna kommer att gå in i varandra. Fortsätt ändå?",
                    "Kopior i rutnät", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        }
        Replace($"{cols} × {rows} kopior, {GCodeTransform.N(sx)} × {GCodeTransform.N(sy)} mm isär (lyft till Z {GCodeTransform.N(info.MaxZ)} mellan kopiorna)",
            GCodeTransform.Grid(Lines(), cols, rows, sx, sy, info.MaxZ));
    }

    private void Replace(string what, GCodeEditResult result)
    {
        if (!result.Ok)
        {
            _problems.Foreground = Bad;
            _problems.Text = "Ändringen gjordes inte:\n" + string.Join("\n", result.Problems.Select(p => "• " + p));
            return;
        }
        _history.Push(_editor.Text); _undo.IsEnabled = true;
        _loading = true;
        _editor.Text = string.Join(Environment.NewLine, result.Lines);
        _loading = false;
        _dirty = true; UpdateTitle(); Check();
        _status.Text = what + ". Spara eller ladda för körning när det ser rätt ut.";
    }

    private void UndoEdit()
    {
        if (_history.Count == 0) return;
        _loading = true; _editor.Text = _history.Pop(); _loading = false;
        _undo.IsEnabled = _history.Count > 0;
        _dirty = true; UpdateTitle(); Check();
        _status.Text = "Ändringen ångrad.";
    }

    private bool Read(TextBox box, string name, out double value)
    {
        if (double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value)) return true;
        _problems.Foreground = Bad; _problems.Text = $"Ange {name} som ett tal i mm.";
        box.Focus();
        return false;
    }

    // ---- Helpers ----

    private static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    private static TextBox Num(string text) => new() { Text = text, Margin = new Thickness(3), Padding = new Thickness(4, 2, 4, 2) };

    private static Button Btn(string text, string glyph, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 4, 10, 4) };
        Ui.SetIcon(b, glyph);
        b.Click += click;
        return b;
    }

    private static FrameworkElement Labeled(string label, Control field) =>
        new StackPanel { Children = { new TextBlock { Text = label, Foreground = Muted, FontSize = 11, Margin = new Thickness(3, 2, 3, 0) }, field } };

    private static void AddCell(Grid grid, FrameworkElement child, int column, int row = 0)
    {
        Grid.SetColumn(child, column); Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private static Border CardOf(string title, UIElement content) => new()
    {
        Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 0, 0, 10),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(0, 0, 0, 6) },
                content
            }
        }
    };

    private sealed class RelayCommand(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
