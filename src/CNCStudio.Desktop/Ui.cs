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

    private static void OnIcon(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl c || e.NewValue is not string glyph || glyph.Length == 0) return;
        c.ContentTemplate = Template(glyph);
        c.HorizontalContentAlignment = HorizontalAlignment.Left;
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
