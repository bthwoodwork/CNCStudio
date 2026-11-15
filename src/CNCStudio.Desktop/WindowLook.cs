using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CNCStudio.Desktop;

/// <summary>
/// One look for every window, applied when it opens (App registers it): a dark title bar (Windows 10 1809+ and 11)
/// and – for the windows CNC Studio builds in code – the button row at the bottom as a footer like the dialogs':
/// its own background, the buttons to the right, the main button (Spara, Lägg till, OK …) in the accent colour.
/// Nothing here changes what a window does; if anything is unexpected it is left as it was.
/// </summary>
internal static class WindowLook
{
    private static readonly Brush Footer = Frozen(0x19, 0x26, 0x34);
    private static readonly Brush FooterLine = Frozen(0x2C, 0x3E, 0x52);
    private static readonly Brush Primary = Frozen(0x2E, 0x7D, 0xD7);

    public static void Apply(Window w)
    {
        try { DarkTitleBar(w); } catch (Exception) { }
        if (w is MainWindow or AppDialog) return;
        try { StyleFooter(w); } catch (Exception) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void DarkTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = 1;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 2004+ / 11); 19 on Windows 10 1809–1909.
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    private static void StyleFooter(Window w)
    {
        if (FindDock(w.Content as DependencyObject, 3) is not DockPanel dock) return;
        foreach (UIElement child in dock.Children)
        {
            if (DockPanel.GetDock(child) != Dock.Bottom || child is not FrameworkElement bottom) continue;
            if (FindButtonRow(bottom, 3) is not Panel row) continue;
            // The footer: its own background and a line above, the buttons right-aligned.
            if (bottom is Panel p) p.Background = Footer;
            else if (bottom is Border b) b.Background = Footer;
            else continue;
            bottom.Margin = new Thickness(0);
            row.HorizontalAlignment = HorizontalAlignment.Right;
            row.Margin = new Thickness(18, 10, 18, 12);
            if (bottom is Panel pp && pp != row) foreach (var t in pp.Children.OfType<FrameworkElement>().Where(c => c != row && c is not Panel))
                t.Margin = new Thickness(18, 8, 18, 0);
            var buttons = row.Children.OfType<Button>().ToList();
            var main = buttons.FirstOrDefault(x => x.IsDefault) ?? buttons.FirstOrDefault(x => x.FontWeight >= FontWeights.SemiBold) ?? (buttons.Count > 1 ? buttons[0] : null);
            foreach (var btn in buttons)
            {
                btn.MinWidth = 92;
                btn.Padding = new Thickness(14, 6, 14, 6);
                btn.Margin = new Thickness(8, 0, 0, 0);
                if (btn == main) { btn.Background = Primary; btn.Foreground = Brushes.White; btn.FontWeight = FontWeights.SemiBold; }
            }
            var line = new Border { Height = 1, Background = FooterLine };
            if (bottom is Panel withLine && withLine is not WrapPanel) { withLine.Children.Insert(0, line); }
            return;
        }
    }

    private static DockPanel? FindDock(DependencyObject? d, int depth)
    {
        if (d is null || depth < 0) return null;
        if (d is DockPanel dp) return dp;
        DependencyObject? inner = d switch { ContentControl c => c.Content as DependencyObject, Decorator b => b.Child, _ => null };
        return FindDock(inner, depth - 1);
    }

    /// <summary>A row of buttons only (WrapPanel, or a horizontal StackPanel) at or just inside the element.</summary>
    private static Panel? FindButtonRow(DependencyObject d, int depth)
    {
        if (depth < 0) return null;
        if (d is Panel p and (WrapPanel or StackPanel { Orientation: Orientation.Horizontal }) && p.Children.Count > 0 && p.Children.OfType<UIElement>().All(c => c is Button))
            return p;
        IEnumerable<DependencyObject> kids = d switch
        {
            Panel panel => panel.Children.OfType<DependencyObject>(),
            Decorator b when b.Child is not null => new[] { (DependencyObject)b.Child },
            _ => Array.Empty<DependencyObject>(),
        };
        foreach (var k in kids) if (FindButtonRow(k, depth - 1) is Panel found) return found;
        return null;
    }

    private static Brush Frozen(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
