using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CNCStudio.Desktop;

/// <summary>
/// Hjälp → Manual: the user manual (Help\Manual.md, built into the program) with a table of contents on the left
/// and the text on the right. The reader's own search box (Ctrl+F) finds words in the whole manual.
/// <para>The manual is written in a small part of Markdown: # and ## headings, paragraphs, "- " and "1. " lists,
/// **bold** and `code`.</para>
/// </summary>
public sealed class ManualWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x9F, 0xB3, 0xC8), Accent = B(0x6F, 0xB6, 0xFF),
        Paper = B(0x14, 0x1F, 0x2B), Side = B(0x1C, 0x2A, 0x39), Code = B(0x24, 0x36, 0x48);

    private readonly ListBox _toc = new()
    {
        Background = Side, Foreground = Ink, BorderThickness = new Thickness(0), Padding = new Thickness(6, 8, 6, 8)
    };
    private readonly FlowDocumentReader _reader = new()
    {
        ViewingMode = FlowDocumentReaderViewingMode.Scroll, IsPageViewEnabled = false, IsTwoPageViewEnabled = false,
        IsFindEnabled = true, Background = Paper, Foreground = Ink
    };

    public ManualWindow(string? startAt = null)
    {
        Title = "CNC Studio – manual";
        Width = 1100; Height = 800; MinWidth = 700; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Paper; Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        var (document, headings) = Build(LoadText());
        _reader.Document = document;
        foreach (var (title, level, block) in headings)
            _toc.Items.Add(new ListBoxItem
            {
                Content = title, Tag = block, Foreground = level == 1 ? Ink : Muted,
                FontWeight = level == 1 ? FontWeights.SemiBold : FontWeights.Normal,
                Padding = new Thickness(level == 1 ? 4 : 18, level == 1 ? 6 : 2, 4, 2), FontSize = level == 1 ? 13.5 : 12.5
            });
        _toc.SelectionChanged += (_, _) => { if ((_toc.SelectedItem as ListBoxItem)?.Tag is Block b) b.BringIntoView(); };

        var side = new DockPanel { Width = 270, Background = Side };
        var label = new TextBlock { Text = "INNEHÅLL", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B(0xA5, 0xC0, 0xD9), Margin = new Thickness(14, 12, 0, 2) };
        DockPanel.SetDock(label, Dock.Top);
        var hint = new TextBlock { Text = "Sök i hela manualen: Ctrl+F (rutan under texten).", FontSize = 10.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 6, 10, 10) };
        DockPanel.SetDock(hint, Dock.Bottom);
        side.Children.Add(label); side.Children.Add(hint); side.Children.Add(_toc);

        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(_reader);
        Content = root;

        if (startAt is not null)
            Loaded += (_, _) =>
            {
                var item = _toc.Items.OfType<ListBoxItem>().FirstOrDefault(i => (i.Content as string)?.Contains(startAt, StringComparison.OrdinalIgnoreCase) == true);
                if (item is not null) _toc.SelectedItem = item;
            };
    }

    private static string LoadText()
    {
        using var stream = typeof(ManualWindow).Assembly.GetManifestResourceStream("CNCStudio.Desktop.Help.Manual.md");
        if (stream is null) return "# Manualen saknas\n\nManualen kunde inte läsas från programmet.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Markdown (the small part the manual uses) → a FlowDocument and its headings for the contents.</summary>
    private static (FlowDocument Document, List<(string Title, int Level, Block Block)> Headings) Build(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Foreground = Ink, Background = Paper,
            PagePadding = new Thickness(36, 24, 36, 40), LineHeight = 22, ColumnWidth = double.PositiveInfinity, MaxPageWidth = 900
        };
        var headings = new List<(string, int, Block)>();
        List? list = null;
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            AddInlines(p.Inlines, string.Join(" ", paragraph));
            doc.Blocks.Add(p);
            paragraph.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var bullet = Regex.Match(line, @"^\s*(?:-|(\d+)\.)\s+(.*)$");
            if (line.StartsWith("# ") || line.StartsWith("## "))
            {
                FlushParagraph(); list = null;
                int level = line.StartsWith("## ") ? 2 : 1;
                string title = line[(level + 1)..].Trim();
                var h = new Paragraph(new Run(title))
                {
                    FontSize = level == 1 ? 24 : 17, FontWeight = FontWeights.SemiBold,
                    Foreground = level == 1 ? Ink : Accent,
                    Margin = new Thickness(0, level == 1 ? (doc.Blocks.Count == 0 ? 0 : 26) : 16, 0, 8)
                };
                if (level == 1 && doc.Blocks.Count > 0)
                    doc.Blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = B(0x2C, 0x3E, 0x52), Margin = new Thickness(0, 14, 0, 0) }));
                doc.Blocks.Add(h);
                headings.Add((title, level, h));
            }
            else if (bullet.Success)
            {
                FlushParagraph();
                bool numbered = bullet.Groups[1].Success;
                if (list is null || (list.MarkerStyle == TextMarkerStyle.Decimal) != numbered)
                {
                    list = new List
                    {
                        MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(22, 0, 0, 0)
                    };
                    doc.Blocks.Add(list);
                }
                var item = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                AddInlines(item.Inlines, bullet.Groups[2].Value);
                list.ListItems.Add(new ListItem(item));
            }
            else if (line.Length == 0) { FlushParagraph(); list = null; }
            else { list = null; paragraph.Add(line.Trim()); }
        }
        FlushParagraph();
        return (doc, headings);
    }

    /// <summary>**bold** and `code` inside a line.</summary>
    private static void AddInlines(InlineCollection inlines, string text)
    {
        foreach (Match m in Regex.Matches(text, @"\*\*(.+?)\*\*|`(.+?)`|[^*`]+|[*`]"))
        {
            if (m.Groups[1].Success) inlines.Add(new Bold(new Run(m.Groups[1].Value)));
            else if (m.Groups[2].Success)
                inlines.Add(new Run(m.Groups[2].Value) { FontFamily = new FontFamily("Consolas"), Background = Code, FontSize = 13 });
            else inlines.Add(new Run(m.Value));
        }
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}
