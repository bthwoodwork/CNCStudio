using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clipper2Lib;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Rita → Bild till vektor…: the picture with the traced outline on top, and the threshold, invert, smoothing,
/// speck size and width in mm – the outline follows every change.
/// </summary>
public sealed class ImageTraceWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x8C, 0xA3, 0xBA), Card = B(0x1C, 0x2A, 0x39), Bad = B(0xFF, 0x7A, 0x86);

    public PathsD? Traced { get; private set; }
    public double WidthMm { get; private set; }

    private readonly byte[] _gray;
    private readonly int _w, _h;
    private readonly Slider _threshold = new() { Minimum = 1, Maximum = 254, Value = 128, Width = 220 };
    private readonly Slider _smooth = new() { Minimum = 0, Maximum = 4, Value = 1, Width = 220 };
    private readonly Slider _speck = new() { Minimum = 0, Maximum = 400, Value = 16, Width = 220 };
    private readonly CheckBox _invert = new() { Content = "Invertera (ljusa delar är formen)", Foreground = Ink };
    private readonly CheckBox _showImage = new() { Content = "Visa bilden under", Foreground = Ink, IsChecked = true };
    private readonly TextBox _width = new() { Width = 70, Padding = new Thickness(4, 2, 4, 2) };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Path _outline = new() { Fill = new SolidColorBrush(Color.FromArgb(150, 0x2E, 0x9C, 0xF0)), Stroke = Brushes.OrangeRed, StrokeThickness = 1 };
    private readonly TextBlock _info = new() { Foreground = Ink, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };

    public ImageTraceWindow(BitmapSource image, byte[] gray, int w, int h, string fileName, double suggestedWidthMm)
    {
        _gray = gray; _w = w; _h = h;
        Title = "Bild till vektor – " + fileName;
        Width = 980; Height = 720; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        _image.Source = image;
        _width.Text = suggestedWidthMm.ToString("0", CultureInfo.InvariantCulture);
        var canvas = new Grid { Width = w, Height = h, Background = Brushes.White };
        canvas.Children.Add(_image);
        canvas.Children.Add(new Canvas { Children = { _outline } });
        var view = new Border { Background = Card, Padding = new Thickness(10), Child = new Viewbox { Child = canvas, Stretch = Stretch.Uniform } };

        var side = new StackPanel { Width = 300, Margin = new Thickness(14, 0, 0, 0) };
        side.Children.Add(new TextBlock
        {
            Text = "Bilden görs svartvit vid tröskeln och konturen spåras. Bäst blir logotyper, teckningar och silhuetter med tydliga kanter – inte foton.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        side.Children.Add(Label("Tröskel – mörkare än detta är formen")); side.Children.Add(_threshold);
        side.Children.Add(_invert);
        side.Children.Add(Label("Utjämning (färre trappsteg och punkter)")); side.Children.Add(_smooth);
        side.Children.Add(Label("Ta bort prickar och hål mindre än (pixlar)")); side.Children.Add(_speck);
        side.Children.Add(Label("Bredd i projektet (mm)")); side.Children.Add(_width);
        side.Children.Add(_showImage);
        side.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 0), Child = _info });
        side.Children.Add(_error);
        var ok = new Button { Content = "Lägg till i projektet", FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Avbryt", IsCancel = true };
        ok.Click += (_, _) => Save();
        side.Children.Add(new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel } });

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = side });
        root.Children.Add(view);
        Content = root;

        _debounce.Tick += (_, _) => { _debounce.Stop(); Retrace(); };
        void Later() { _debounce.Stop(); _debounce.Start(); }
        _threshold.ValueChanged += (_, _) => Later(); _smooth.ValueChanged += (_, _) => Later(); _speck.ValueChanged += (_, _) => Later();
        _invert.Click += (_, _) => Later();
        _width.TextChanged += (_, _) => ShowInfo();
        _showImage.Click += (_, _) => _image.Visibility = _showImage.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        Retrace();
    }

    private TraceOptions Options => new()
    {
        Threshold = (int)Math.Round(_threshold.Value), Invert = _invert.IsChecked == true, SmoothPx = _smooth.Value, MinAreaPx = _speck.Value,
    };

    private void Retrace()
    {
        try
        {
            Traced = ImageTrace.Trace(_gray, _w, _h, Options);
            var g = new StreamGeometry { FillRule = System.Windows.Media.FillRule.EvenOdd };
            using (var c = g.Open())
                foreach (var p in Traced)
                {
                    c.BeginFigure(new Point(p[0].x, p[0].y), true, true);
                    c.PolyLineTo(p.Skip(1).Select(v => new Point(v.x, v.y)).ToList(), true, false);
                }
            g.Freeze();
            _outline.Data = g;
            _error.Text = "";
        }
        catch (Exception ex) { Traced = null; _outline.Data = null; _error.Text = ex.Message; }
        ShowInfo();
    }

    private void ShowInfo()
    {
        if (Traced is null || Traced.Count == 0) { _info.Text = "Inget spårat – ändra tröskeln eller kryssa i Invertera."; return; }
        double x0 = Traced.Min(p => p.Min(v => v.x)), x1 = Traced.Max(p => p.Max(v => v.x));
        double y0 = Traced.Min(p => p.Min(v => v.y)), y1 = Traced.Max(p => p.Max(v => v.y));
        string size = double.TryParse(_width.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) && mm > 0
            ? $"{mm:0.#} × {mm * (y1 - y0) / Math.Max(x1 - x0, 1e-6):0.#} mm" : "ange bredden";
        int points = ImageTrace.PointCount(Traced);
        _info.Text = $"{Traced.Count} konturer, {points} punkter · {size}." +
            (points > CamPlanner.AutoRecomputePointLimit ? "\nMånga punkter: körbanorna räknas med Beräkna körbanor (öka utjämningen för färre)." : "");
    }

    private void Save()
    {
        if (Traced is null || Traced.Count == 0) { _error.Text = "Inget att lägga till."; return; }
        if (!double.TryParse(_width.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) || mm < 1 || mm > 5000)
        { _error.Text = "Bredden måste vara 1–5000 mm."; return; }
        WidthMm = mm;
        DialogResult = true;
    }

    private static TextBlock Label(string t) => new() { Text = t, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 8, 0, 2) };
    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
