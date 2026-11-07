using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CNCStudio.Core.GCode;

namespace CNCStudio.Desktop;

/// <summary>
/// A job with several tools is several programs (no tool changer). Lists them in run order so the user
/// picks the one to load; each needs its tool fitted and Z zeroed with it first.
/// </summary>
public sealed class ProgramPickerWindow : Window
{
    public GCodeJob? Selected { get; private set; }

    public ProgramPickerWindow(IReadOnlyList<GCodeJob> jobs, int suggested)
    {
        Title = "Välj program";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x21, 0x2C));
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xF1, 0xFA));
        FontFamily = new FontFamily("Segoe UI");
        var muted = new SolidColorBrush(Color.FromRgb(0x8C, 0xA3, 0xBA));

        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var job in jobs)
        {
            string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
            var depth = job.Operations.Max(o => o.Operation.TotalDepth);
            var b = new Button
            {
                Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(12, 8, 12, 8), Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Foreground = Brushes.White,
                Background = new SolidColorBrush(job.Number == suggested ? Color.FromRgb(0x24, 0x61, 0x8C) : Color.FromRgb(0x34, 0x47, 0x5A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x67, 0x83)),
                Content = new StackPanel { Children =
                {
                    new TextBlock { Text = $"{job.Number}.  {job.ToolLabel}", FontSize = 15, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = $"{job.Operations.Count} operation(er), djupast {F(depth)} mm, {job.Result.Lines.Count} rader",
                        Foreground = new SolidColorBrush(Color.FromRgb(0xD5, 0xE6, 0xF5)), FontSize = 12 }
                } }
            };
            var j = job;
            b.Click += (_, _) => { Selected = j; DialogResult = true; };
            list.Children.Add(b);
        }
        var cancel = new Button { Content = "Avbryt", IsCancel = true, Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(0x34, 0x47, 0x5A)), Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };
        Content = new StackPanel { Margin = new Thickness(18), Children =
        {
            new TextBlock { Text = $"PROJEKTET ANVÄNDER {jobs.Count} VERKTYG", FontSize = 16, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Maskinen har ingen verktygsväxlare, så varje verktyg är ett eget program. Kör dem i ordning. " +
                                   "Före varje program: sätt i verktyget och nolla Z på materialytan. X0 Y0 ändras inte.",
                TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 4, 0, 0) },
            list, cancel
        } };
    }
}
