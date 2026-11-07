using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// The program opens blank: a start view over the whole window with "Nytt projekt", "Öppna projekt…"
/// and the most recently saved projects in the Projekt folder. Nothing is drawn until a project is
/// opened or created; Ctrl+N / Ctrl+O and the menu also close the start view.
/// </summary>
public partial class MainWindow
{
    /// <summary>How many recent projects the start view lists.</summary>
    private const int StartScreenRecentCount = 12;

    private Border? _startScreen;
    private Grid? _startHost;

    internal bool StartScreenVisible => _startScreen is { Visibility: Visibility.Visible };

    private void ShowStartScreen()
    {
        if (_startHost is null)
        {
            // Put the window's content and the start view in one grid, the start view on top.
            var content = (UIElement)Content;
            Content = null;
            _startHost = new Grid();
            _startHost.Children.Add(content);
            Content = _startHost;
        }
        if (_startScreen is not null) _startHost.Children.Remove(_startScreen);
        _startScreen = BuildStartScreen();
        _startHost.Children.Add(_startScreen);
        StatusText.Text = "Öppna ett projekt eller skapa ett nytt.";
    }

    private void HideStartScreen()
    {
        if (_startScreen is not null) _startScreen.Visibility = Visibility.Collapsed;
    }

    private Border BuildStartScreen()
    {
        var ink = new SolidColorBrush(Color.FromRgb(0xE8, 0xF1, 0xFA));
        var soft = new SolidColorBrush(Color.FromRgb(0xA5, 0xC0, 0xD9));
        var muted = new SolidColorBrush(Color.FromRgb(0x8C, 0xA3, 0xBA));
        var panel = new SolidColorBrush(Color.FromRgb(0x1F, 0x2E, 0x3F));

        Button Big(string icon, string text, string sub, RoutedEventHandler click)
        {
            var b = new Button
            {
                Width = 250, Height = 92, Margin = new Thickness(0, 0, 14, 0), Padding = new Thickness(16, 10, 16, 10),
                Background = new SolidColorBrush(Color.FromRgb(0x24, 0x61, 0x8C)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0x8F, 0xC4)),
                Foreground = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
                {
                    new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 26, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center },
                    new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
                    {
                        new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = sub, FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0xD5, 0xE6, 0xF5)), TextWrapping = TextWrapping.Wrap, MaxWidth = 170 }
                    } }
                } }
            };
            b.Click += click;
            return b;
        }

        var recentList = new StackPanel();
        var projects = ProjectCatalog.Recent(AppStorage.Current.ProjectsFolder, StartScreenRecentCount);
        if (projects.Count == 0)
            recentList.Children.Add(new TextBlock { Text = "Inga sparade projekt i Projekt-mappen ännu.", Foreground = muted, Margin = new Thickness(4, 6, 4, 6) });
        foreach (var p in projects)
        {
            var row = new Button
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = ink, Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(10, 7, 10, 7), HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand,
                ToolTip = p.Path,
                Content = new DockPanel { LastChildFill = true, Children =
                {
                    Docked(new TextBlock { Text = p.LastWrite.ToString("yyyy-MM-dd HH:mm"), Foreground = muted, Margin = new Thickness(16, 0, 0, 0) }, Dock.Right),
                    new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Inlines =
                    {
                        new System.Windows.Documents.Run(p.Name) { FontWeight = FontWeights.SemiBold },
                        new System.Windows.Documents.Run(p.Subfolder is null ? "" : "   " + p.Subfolder) { Foreground = muted }
                    } }
                } }
            };
            row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x45, 0x5E));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var path = p.Path;
            row.Click += (_, _) => OpenProject(path);
            recentList.Children.Add(row);
        }

        var folderLink = new TextBlock { Text = AppStorage.Current.ProjectsFolder, Foreground = muted, FontSize = 11.5, Margin = new Thickness(0, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };

        var body = new StackPanel { Width = 760, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new TextBlock { Text = "CNC STUDIO", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = ink },
            new TextBlock { Text = AppTitle.Replace("CNC Studio ", "Version "), Foreground = muted, Margin = new Thickness(0, 2, 0, 24) },
            new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 28), Children =
            {
                Big("", "Nytt projekt", "Tomt projekt med maskinprofilen", New_Click),
                Big("", "Öppna projekt…", "Välj en projektfil", Open_Click)
            } },
            new TextBlock { Text = "SENASTE PROJEKT", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = soft, Margin = new Thickness(0, 0, 0, 6) },
            new Border { Background = panel, CornerRadius = new CornerRadius(6), Padding = new Thickness(6),
                Child = new ScrollViewer { MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = recentList } },
            folderLink
        } };

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x21, 0x2C)),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body }
        };
    }

    private static UIElement Docked(UIElement e, Dock dock) { DockPanel.SetDock(e, dock); return e; }
}
