using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CNCStudio.Desktop;

/// <summary>Arkiv → Exportera 3D (STL)…: thickness, with or without dogbones, one file or one per part.</summary>
public sealed class StlExportWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x9F, 0xB3, 0xC8), Bad = B(0xFF, 0x7A, 0x86);

    private readonly TextBox _thickness = new() { Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(5, 3, 5, 3), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _milled = new()
    {
        Content = "Som det fräses – med dogbones och fräsens radie i innerhörnen", Foreground = Ink, Margin = new Thickness(0, 4, 0, 2),
        ToolTip = "Av: formen som den är ritad, med skarpa hörn – det man vill ha för 3D-utskrift. På: delen som fräsen lämnar den, där banorna är beräknade."
    };
    private readonly CheckBox _perPart = new() { Content = "En fil per del", Foreground = Ink, Margin = new Thickness(0, 6, 0, 2), IsChecked = true };
    private readonly TextBlock _error = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap };

    public double Thickness { get; private set; }
    public bool AsMilled => _milled.IsChecked == true;
    public bool PerPart => _perPart.IsChecked == true;

    public StlExportWindow(int shapes, int parts, double thickness, bool anyDogbones)
    {
        Title = "Exportera 3D (STL)";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");
        _thickness.Text = thickness.ToString("0.###", CultureInfo.InvariantCulture);

        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        root.Children.Add(new TextBlock
        {
            Text = $"{parts} {(parts == 1 ? "del" : "delar")} av {shapes} {(shapes == 1 ? "objekt" : "objekt")} blir solida kroppar – konturen rest till tjockleken nedan, i mm. " +
                   "Former med invändig genomskärning blir hål i delen de ligger på. Fickor, gravyr, V-carve och borrhål tas inte med.",
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 10)
        });
        root.Children.Add(new TextBlock { Text = "Tjocklek (mm)", Foreground = Muted, FontSize = 11 });
        root.Children.Add(_thickness);
        root.Children.Add(_milled);
        root.Children.Add(new TextBlock
        {
            Text = anyDogbones
                ? "Dogbones behövs bara för fräsen. Lämna rutan tom för en ren del till 3D-utskrift."
                : "Inga av delarna har dogbones. Rutan ger ändå fräsens radie i innerhörnen.",
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 0, 0, 4)
        });
        root.Children.Add(_perPart);
        root.Children.Add(_error);
        var ok = new Button { Content = "Exportera…", FontWeight = FontWeights.SemiBold, Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Avbryt", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (!double.TryParse(_thickness.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) || t <= 0 || t > 1000)
            { _error.Text = "Tjockleken ska vara ett antal mm större än 0."; _thickness.Focus(); return; }
            Thickness = t; DialogResult = true;
        };
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } });
        Content = root;
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}
