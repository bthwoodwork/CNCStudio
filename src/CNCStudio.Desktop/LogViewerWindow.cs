using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Help → Loggar och fellogg: the error log as a list (newest first) with full details, plus the
/// machine-control logs, first-test logs and $$ backups – for every computer that writes to the same
/// CNC Studio folder (Loggar\<dator>). Search, copy and "open folder". Read-only except for clearing this
/// computer's error log after confirmation.
/// </summary>
public sealed class LogViewerWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        PanelBg = B(0x1F, 0x2E, 0x3F), Field = B(0x10, 0x1B, 0x27), Edge = B(0x3C, 0x51, 0x67), Bad = B(0xFF, 0x7A, 0x86);

    private readonly ListBox _files = new();
    private readonly ListBox _entries = new() { MaxHeight = 230 };
    private readonly TextBox _text = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = Field, Foreground = Ink, BorderBrush = Edge, Padding = new Thickness(8)
    };
    private readonly TextBox _search = new() { Width = 220, Padding = new Thickness(5, 3, 5, 3) };
    private readonly TextBlock _heading = new() { FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _clear = new() { Content = "Rensa felloggen…" };
    private readonly ComboBox _computer = new() { MinWidth = 220, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _folderText = new() { Foreground = B(0x8C, 0xA3, 0xBA), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private string Computer => _computer.SelectedItem as string ?? AppStorage.ComputerName;
    private string ComputerFolder => Path.Combine(AppStorage.Current.LogsRoot, Computer);
    private string ErrorFile => Path.Combine(ComputerFolder, "fel.txt");
    private bool IsThisComputer => Computer.Equals(AppStorage.ComputerName, StringComparison.OrdinalIgnoreCase);
    private IReadOnlyList<ErrorLogEntry> _errors = Array.Empty<ErrorLogEntry>();
    private string _rawText = "";

    public LogViewerWindow()
    {
        Title = "Loggar och fellogg";
        Width = 1100; Height = 720; MinWidth = 800; MinHeight = 480;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildLayout();
        _files.SelectionChanged += (_, _) => ShowSelected();
        _entries.SelectionChanged += (_, _) => ShowEntry();
        _search.TextChanged += (_, _) => ApplySearch();
        LoadComputers();
        _computer.SelectionChanged += (_, _) => LoadFiles(selectErrorLog: true);
        LoadFiles(selectErrorLog: true);
    }

    private UIElement BuildLayout()
    {
        StyleList(_files); StyleList(_entries);
        var header = new StackPanel { Margin = new Thickness(16, 12, 16, 8), Children =
        {
            new TextBlock { Text = "LOGGAR", FontSize = 20, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Fellogg, maskinstyrningens loggar, Första test och säkerhetskopior av maskinens inställningar ($$). Varje dator har en egen mapp under Loggar – med en synkmapp (Google Drive) syns alla datorers loggar här.",
                Foreground = Soft, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children =
                { new TextBlock { Text = "Dator:", VerticalAlignment = VerticalAlignment.Center, Foreground = Soft }, _computer, _folderText } }
        } };
        var left = new Border { Width = 270, Margin = new Thickness(16, 0, 10, 0), Background = PanelBg, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6), Child = _files };
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Children =
            { new TextBlock { Text = "Sök:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = Soft }, _search } };
        var right = new DockPanel();
        DockPanel.SetDock(_heading, Dock.Top); right.Children.Add(_heading);
        DockPanel.SetDock(searchRow, Dock.Top); right.Children.Add(searchRow);
        DockPanel.SetDock(_entries, Dock.Top); right.Children.Add(_entries);
        _entries.Margin = new Thickness(0, 0, 0, 8);
        right.Children.Add(_text);
        var rightBox = new Border { Background = PanelBg, CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0, 0, 16, 0), Child = right };

        var refresh = new Button { Content = "Uppdatera" };
        var copy = new Button { Content = "Kopiera" };
        var open = new Button { Content = "Öppna loggmappen" };
        var close = new Button { Content = "Stäng", IsCancel = true };
        refresh.Click += (_, _) => { LoadComputers(); LoadFiles(selectErrorLog: false); };
        copy.Click += (_, _) => { if (_text.Text.Length > 0) Clipboard.SetText(_text.Text); };
        open.Click += (_, _) =>
        {
            var folder = Directory.Exists(ComputerFolder) ? ComputerFolder : ErrorLog.Folder;
            Directory.CreateDirectory(folder); System.Diagnostics.Process.Start("explorer.exe", folder);
        };
        close.Click += (_, _) => Close();
        _clear.Click += (_, _) => ClearErrorLog();
        var buttons = new WrapPanel { Margin = new Thickness(16, 10, 16, 14) };
        foreach (var b in new[] { refresh, copy, open, _clear, close })
        {
            b.Padding = new Thickness(14, 6, 14, 6); b.Margin = new Thickness(0, 0, 8, 0);
            b.Background = B(0x34, 0x47, 0x5A); b.Foreground = Brushes.White; b.BorderBrush = B(0x50, 0x67, 0x83);
            buttons.Children.Add(b);
        }

        var middle = new DockPanel();
        DockPanel.SetDock(left, Dock.Left); middle.Children.Add(left); middle.Children.Add(rightBox);
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        root.Children.Add(middle);
        return root;
    }

    /// <summary>This computer first, then every other computer with a folder under Loggar.</summary>
    private void LoadComputers()
    {
        var current = _computer.SelectedItem as string;
        var names = LogCatalog.Computers(AppStorage.Current.LogsRoot, AppStorage.ComputerName);
        _computer.ItemsSource = names;
        _computer.ItemTemplate = null;
        _computer.SelectedItem = names.FirstOrDefault(n => n == current) ?? names[0];
    }

    private void LoadFiles(bool selectErrorLog)
    {
        var previous = Selected;
        _folderText.Text = (IsThisComputer ? "den här datorn · " : "") + ComputerFolder;
        var list = LogCatalog.Files(AppStorage.Current.LogsRoot, Computer);
        try { _errors = File.Exists(ErrorFile) ? ErrorLog.Parse(File.ReadAllText(ErrorFile)) : Array.Empty<ErrorLogEntry>(); }
        catch (Exception) { _errors = Array.Empty<ErrorLogEntry>(); }
        _files.Items.Clear();
        LogKind? group = null;
        foreach (var f in list)
        {
            if (f.Kind != group)
            {
                group = f.Kind;
                _files.Items.Add(new ListBoxItem { Content = f.Kind switch
                    {
                        LogKind.Error => "FEL", LogKind.Machine => "MASKINSTYRNING", LogKind.FirstTest => "FÖRSTA TEST", _ => "INSTÄLLNINGAR ($$)"
                    },
                    IsEnabled = false, Foreground = B(0xA5, 0xC0, 0xD9), FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
            }
            var item = new ListBoxItem { Content = TitleOf(f), Tag = f, Padding = new Thickness(8, 4, 8, 4),
                Foreground = f.Kind == LogKind.Error && _errors.Count > 0 ? Bad : Ink };
            _files.Items.Add(item);
        }
        var select = _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is LogFileInfo lf &&
            (selectErrorLog || previous is null ? lf.Kind == LogKind.Error : lf.Path == previous.Path));
        _files.SelectedItem = select ?? _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is LogFileInfo);
    }

    private string TitleOf(LogFileInfo f)
    {
        if (f.Kind == LogKind.Error) return _errors.Count == 0 ? "Fellogg (inga fel)" : $"Fellogg ({_errors.Count} fel)";
        var when = LogCatalog.StampOf(f.Path)?.ToString("yyyy-MM-dd HH:mm") ?? Path.GetFileNameWithoutExtension(f.Path);
        return f.Kind switch { LogKind.Machine => "Session " + when, LogKind.FirstTest => "Första test " + when, _ => "Kopia av $$ " + when };
    }

    private LogFileInfo? Selected => (_files.SelectedItem as ListBoxItem)?.Tag as LogFileInfo;

    private void ShowSelected()
    {
        var f = Selected;
        if (f is null) return;
        bool error = f.Kind == LogKind.Error;
        _entries.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        _clear.IsEnabled = error && _errors.Count > 0 && IsThisComputer;     // other computers' logs are read-only here
        if (error)
        {
            _heading.Text = (_errors.Count == 0 ? "Fellogg – inga fel har loggats" : $"Fellogg – {_errors.Count} fel, senaste först") + (IsThisComputer ? "" : $"  ({Computer})");
            ApplySearch();
            return;
        }
        _heading.Text = TitleOf(f) + (IsThisComputer ? "" : $"  ({Computer})");
        _text.TextWrapping = TextWrapping.NoWrap;                           // logs keep one line per message
        try { _rawText = File.ReadAllText(f.Path); } catch (Exception ex) { _rawText = "Kunde inte läsa filen: " + ex.Message; }
        ApplySearch();
        _text.ScrollToEnd();
    }

    /// <summary>Error log: filters the entries. Text logs: shows only lines containing the search text.</summary>
    private void ApplySearch()
    {
        var q = _search.Text.Trim();
        if (Selected?.Kind == LogKind.Error)
        {
            _entries.Items.Clear();
            foreach (var e in _errors.Where(e => q.Length == 0 || e.Details.Contains(q, StringComparison.OrdinalIgnoreCase)))
                _entries.Items.Add(new ListBoxItem
                {
                    Tag = e, Padding = new Thickness(6, 4, 6, 4),
                    Content = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Inlines =
                    {
                        new System.Windows.Documents.Run(e.Time.ToString("yyyy-MM-dd HH:mm:ss") + "   ") { Foreground = Muted },
                        new System.Windows.Documents.Run(e.Kind + "   ") { Foreground = Bad, FontWeight = FontWeights.SemiBold },
                        new System.Windows.Documents.Run(e.Message) { Foreground = Ink }
                    } }
                });
            if (_entries.Items.Count > 0) _entries.SelectedIndex = 0;
            else _text.Text = _errors.Count == 0
                ? "Inga oväntade fel har loggats. Om programmet stöter på ett fel visas det här med tid, typ och detaljer."
                : "Inga fel matchar sökningen.";
            return;
        }
        _text.Text = q.Length == 0 ? _rawText
            : string.Join(Environment.NewLine, _rawText.Replace("\r\n", "\n").Split('\n').Where(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private void ShowEntry()
    {
        if ((_entries.SelectedItem as ListBoxItem)?.Tag is not ErrorLogEntry e) return;
        _text.TextWrapping = TextWrapping.Wrap;                         // details read best wrapped
        _text.Text = $"{e.Time:yyyy-MM-dd HH:mm:ss}   {e.Kind}{Environment.NewLine}{e.Message}{Environment.NewLine}{Environment.NewLine}{e.Details}";
        _text.ScrollToHome();
    }

    private void ClearErrorLog()
    {
        if (!IsThisComputer || !File.Exists(ErrorLog.FilePath)) return;
        if (AppDialog.Show(this, $"Rensa felloggen ({_errors.Count} fel)? Detaljerna går inte att få tillbaka. Kopiera dem först om du vill skicka dem vidare.",
                "Rensa felloggen", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        File.Delete(ErrorLog.FilePath);
        LoadFiles(selectErrorLog: true);
    }

    private static void StyleList(ListBox list)
    {
        list.Background = Field; list.Foreground = Ink; list.BorderBrush = Edge;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
