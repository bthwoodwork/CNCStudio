using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Help → Loggar och fellogg: the error log as a list (newest first) with full details, plus the
/// first-test logs and the $$ backups, with search, copy and "open folder". Read-only except for
/// clearing the error log after confirmation.
/// </summary>
public sealed class LogViewerWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        PanelBg = B(0x1F, 0x2E, 0x3F), Field = B(0x10, 0x1B, 0x27), Edge = B(0x3C, 0x51, 0x67), Bad = B(0xFF, 0x7A, 0x86);

    private sealed record LogFile(string Path, string Title, string Kind, DateTime Time);

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
        LoadFiles(selectErrorLog: true);
    }

    private UIElement BuildLayout()
    {
        Style(_files); Style(_entries);
        var header = new StackPanel { Margin = new Thickness(16, 12, 16, 8), Children =
        {
            new TextBlock { Text = "LOGGAR", FontSize = 20, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Fellogg, loggar från Första test och säkerhetskopior av maskinens inställningar ($$). Mapp: " + ErrorLog.Folder,
                Foreground = Soft, TextWrapping = TextWrapping.Wrap }
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
        refresh.Click += (_, _) => LoadFiles(selectErrorLog: false);
        copy.Click += (_, _) => { if (_text.Text.Length > 0) Clipboard.SetText(_text.Text); };
        open.Click += (_, _) => { Directory.CreateDirectory(ErrorLog.Folder); System.Diagnostics.Process.Start("explorer.exe", ErrorLog.Folder); };
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

    private void LoadFiles(bool selectErrorLog)
    {
        var previous = (_files.SelectedItem as ListBoxItem)?.Tag as LogFile;
        var list = new List<LogFile>();
        _errors = File.Exists(ErrorLog.FilePath) ? ErrorLog.Parse(File.ReadAllText(ErrorLog.FilePath)) : Array.Empty<ErrorLogEntry>();
        list.Add(new LogFile(ErrorLog.FilePath, _errors.Count == 0 ? "Fellogg (inga fel)" : $"Fellogg ({_errors.Count} fel)", "error", DateTime.MaxValue));
        if (Directory.Exists(ErrorLog.Folder))
        {
            foreach (var f in Directory.GetFiles(ErrorLog.Folder, "forsta-test-*.txt"))
                list.Add(new LogFile(f, "Första test " + Stamp(f), "test", File.GetLastWriteTime(f)));
            foreach (var f in Directory.GetFiles(ErrorLog.Folder, "$$-backup-*.txt"))
                list.Add(new LogFile(f, "Kopia av $$ " + Stamp(f), "backup", File.GetLastWriteTime(f)));
        }
        _files.Items.Clear();
        string? group = null;
        foreach (var f in list.OrderBy(f => f.Kind == "error" ? 0 : f.Kind == "test" ? 1 : 2).ThenByDescending(f => f.Time))
        {
            if (f.Kind != group)
            {
                group = f.Kind;
                _files.Items.Add(new ListBoxItem { Content = group switch { "error" => "FEL", "test" => "FÖRSTA TEST", _ => "INSTÄLLNINGAR ($$)" },
                    IsEnabled = false, Foreground = B(0xA5, 0xC0, 0xD9), FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
            }
            var item = new ListBoxItem { Content = f.Title, Tag = f, Padding = new Thickness(8, 4, 8, 4),
                Foreground = f.Kind == "error" && _errors.Count > 0 ? Bad : Ink };
            _files.Items.Add(item);
        }
        var select = _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is LogFile lf &&
            (selectErrorLog || previous is null ? lf.Kind == "error" : lf.Path == previous.Path));
        _files.SelectedItem = select ?? _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is LogFile);
    }

    private static string Stamp(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var stamp = name[(name.LastIndexOf('-', name.LastIndexOf('-') - 1) + 1)..];   // yyyyMMdd-HHmmss
        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("yyyy-MM-dd HH:mm") : name;
    }

    private LogFile? Selected => (_files.SelectedItem as ListBoxItem)?.Tag as LogFile;

    private void ShowSelected()
    {
        var f = Selected;
        if (f is null) return;
        bool error = f.Kind == "error";
        _entries.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        _clear.IsEnabled = error && _errors.Count > 0;
        if (error)
        {
            _heading.Text = _errors.Count == 0 ? "Fellogg – inga fel har loggats" : $"Fellogg – {_errors.Count} fel, senaste först";
            ApplySearch();
            return;
        }
        _heading.Text = f.Title;
        _text.TextWrapping = TextWrapping.NoWrap;                           // logs keep one line per message
        try { _rawText = File.ReadAllText(f.Path); } catch (Exception ex) { _rawText = "Kunde inte läsa filen: " + ex.Message; }
        ApplySearch();
        _text.ScrollToEnd();
    }

    /// <summary>Error log: filters the entries. Text logs: shows only lines containing the search text.</summary>
    private void ApplySearch()
    {
        var q = _search.Text.Trim();
        if (Selected?.Kind == "error")
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
        if (!File.Exists(ErrorLog.FilePath)) return;
        if (AppDialog.Show(this, $"Rensa felloggen ({_errors.Count} fel)? Detaljerna går inte att få tillbaka. Kopiera dem först om du vill skicka dem vidare.",
                "Rensa felloggen", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        File.Delete(ErrorLog.FilePath);
        LoadFiles(selectErrorLog: true);
    }

    private static void Style(ListBox list)
    {
        list.Background = Field; list.Foreground = Ink; list.BorderBrush = Edge;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
