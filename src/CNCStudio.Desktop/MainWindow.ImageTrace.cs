using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Rita → Bild till vektor…: a PNG/JPG/BMP/GIF/TIFF traced to an outline shape (see <see cref="ImageTrace"/>).</summary>
public partial class MainWindow
{
    private void ImageTrace_Click(object sender, RoutedEventArgs e)
    {
        if (StartScreenVisible) return;
        var dialog = new OpenFileDialog { Title = "Välj en bild att göra till vektor", Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Alla filer|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        BitmapSource image; byte[] gray; int w, h;
        try { (image, gray, w, h) = LoadGray(dialog.FileName); }
        catch (Exception ex) { Error("Bilden kunde inte läsas: " + ex.Message); return; }
        double suggested = _document.HasMaterial ? Math.Round(Math.Min(_document.MaterialWidth, _document.MaterialHeight * w / Math.Max(h, 1)) * 0.6) : 100;
        var tw = new ImageTraceWindow(image, gray, w, h, Path.GetFileName(dialog.FileName), Math.Max(10, suggested)) { Owner = this };
        if (tw.ShowDialog() != true || tw.Traced is null) return;
        DesignShape shape;
        try { shape = ImageTrace.ToShape(tw.Traced, tw.WidthMm, 0, 0); }
        catch (ArgumentException ex) { Error(ex.Message); return; }
        if (_document.HasMaterial) { shape.X = Math.Max(0, (_document.MaterialWidth - shape.Width) / 2); shape.Y = Math.Max(0, (_document.MaterialHeight - shape.Height) / 2); }
        else { shape.X = 10; shape.Y = 10; }
        RememberChange();
        _document.Shapes.Add(shape);
        _selection.Clear(); _selectionOrder.Clear();
        _selection.Add(shape); _selectionOrder.Add(shape.Id); _selected = shape;
        _tool = Tool.Select;
        Render();
        StatusText.Text = $"Bilden spårad: {shape.Contours.Count} konturer, {shape.Width:0.#} × {shape.Height:0.#} mm. Fickfräs, V-carva eller skär ut den som vilken form som helst.";
    }

    /// <summary>The picture scaled to at most ImageTrace.MaxSidePx, and its grey values with transparency counted as white.</summary>
    private static (BitmapSource Image, byte[] Gray, int W, int H) LoadGray(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        BitmapSource src = bmp;
        double scale = Math.Min(1.0, (double)ImageTrace.MaxSidePx / Math.Max(src.PixelWidth, src.PixelHeight));
        if (scale < 1) src = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var gray = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            double a = px[i * 4 + 3] / 255.0;
            double lum = 0.114 * px[i * 4] + 0.587 * px[i * 4 + 1] + 0.299 * px[i * 4 + 2];
            gray[i] = (byte)Math.Round(lum * a + 255 * (1 - a));
        }
        bgra.Freeze();
        return (bgra, gray, w, h);
    }
}
