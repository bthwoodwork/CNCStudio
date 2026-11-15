using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace CNCStudio.Desktop;

/// <summary>
/// <c>local:Ui.Icon="&amp;#xE80F;"</c> on a button: a Segoe icon in front of its text, the same look as the toolbar.
/// The icon is part of the button's content template, so it stays when the code changes the text.
/// </summary>
public static class Ui
{
    /// <summary>Windows 11's icon font, with Windows 10's as fallback (same code points).</summary>
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Ui), new PropertyMetadata(null, OnIcon));

    public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string? value) => d.SetValue(IconProperty, value);

    /// <summary>
    /// <c>local:Ui.Rail="True"</c>: the icon above a short label, centred – for the narrow tool rail (like gSender's
    /// left edge). The button's text is also its tooltip when it has none.
    /// </summary>
    public static readonly DependencyProperty RailProperty =
        DependencyProperty.RegisterAttached("Rail", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnIcon));

    public static bool GetRail(DependencyObject d) => (bool)d.GetValue(RailProperty);
    public static void SetRail(DependencyObject d, bool value) => d.SetValue(RailProperty, value);

    private static void OnIcon(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl c || GetIcon(c) is not string glyph || glyph.Length == 0) return;
        if (GetRail(c))
        {
            c.ContentTemplate = RailTemplate(glyph);
            c.HorizontalContentAlignment = HorizontalAlignment.Center;
            c.Padding = new Thickness(2, 5, 2, 4);
            if (c.ToolTip is null && c.Content is string text) c.ToolTip = text;
        }
        else
        {
            c.ContentTemplate = Template(glyph);
            c.HorizontalContentAlignment = HorizontalAlignment.Left;
        }
    }

    private static DataTemplate RailTemplate(string glyph)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.TextProperty, glyph);
        icon.SetValue(TextBlock.FontFamilyProperty, IconFont);
        icon.SetValue(TextBlock.FontSizeProperty, 18.0);
        icon.SetValue(TextBlock.FontWeightProperty, FontWeights.Normal);
        icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetValue(TextBlock.FontSizeProperty, 10.5);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        panel.AppendChild(icon);
        panel.AppendChild(text);
        var t = new DataTemplate { VisualTree = panel };
        t.Seal();
        return t;
    }

    private static DataTemplate Template(string glyph)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.TextProperty, glyph);
        icon.SetValue(TextBlock.FontFamilyProperty, IconFont);
        icon.SetValue(TextBlock.FontSizeProperty, 14.0);
        icon.SetValue(TextBlock.FontWeightProperty, FontWeights.Normal);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 9, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(UIElement.OpacityProperty, 0.9);
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetBinding(ContentPresenter.ContentProperty, new Binding());
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        panel.AppendChild(icon);
        panel.AppendChild(text);
        var t = new DataTemplate { VisualTree = panel };
        t.Seal();
        return t;
    }
}
