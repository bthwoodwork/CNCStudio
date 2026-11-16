using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Fräsbyte i ett flöde: between the programs of a multi-tool job, one window walks through the change –
/// to the tool-change place, change the cutter, back over the probe plate, probe Z (the next program is then
/// loaded and checked against the new Z), start. Not modal, so the main window's jog, STOPP and keyboard
/// stay usable; the Z height over the plate is always jogged by hand, since a longer cutter could otherwise
/// be driven into the plate.
/// </summary>
public sealed class ToolChangeWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x9F, 0xB3, 0xC8), Card = B(0x1C, 0x2A, 0x39),
        Done = B(0x7C, 0xD9, 0x9C), Bad = B(0xFF, 0x7A, 0x86), Active = B(0x3D, 0xD6, 0xF5);

    /// <summary>What the main window does for each step; each returns an error text, or null when it went well.</summary>
    public sealed record Steps(
        Func<Task<string?>>? GoToChange, string ChangePlace,
        Func<Task<string?>>? GoToProbePoint,
        Func<Task<(bool Ok, string Message)>> ProbeZ,
        Func<bool> Start);

    private readonly Steps _steps;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 10, 0, 0) };
    private readonly List<(Border Card, TextBlock Mark)> _cards = new();
    private readonly Button _go = Btn("Åk dit"), _changed = Btn("Fräsen är bytt"), _back = Btn("Åk tillbaka till probpunkten"),
        _probe = Btn("Proba Z"), _start = Btn("Starta program");
    private bool _busy;

    public ToolChangeWindow(int done, int next, int count, string tool, bool manualSpindle, Steps steps)
    {
        _steps = steps;
        Title = $"Fräsbyte – program {next} av {count}";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");
        _start.Content = $"Starta program {next}";
        _start.FontWeight = FontWeights.SemiBold;
        _probe.FontWeight = FontWeights.SemiBold;

        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        root.Children.Add(new TextBlock
        {
            Text = $"Program {done} av {count} är klart. Nästa: {tool}.", FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(new TextBlock
        {
            Text = (manualSpindle ? "Stäng av fräsen först. " : "") +
                   "Stegen tas i tur och ordning, men du kan jogga och använda STOPP i huvudfönstret hela tiden.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10)
        });

        root.Children.Add(StepCard("1. Till fräsbytet",
            steps.GoToChange is null
                ? "Det finns ingen sparad plats som heter Fräsbyte. Knappen lyfter Z till toppen – jogga sedan dit det är lätt att byta. (Lägg till platsen under Maskin → Platser….)"
                : $"Maskinen lyfter Z och åker till {steps.ChangePlace}.", _go));
        root.Children.Add(StepCard("2. Byt fräs", $"Sätt i {tool}. Starta inte fräsen.", _changed));
        root.Children.Add(StepCard("3. Över probplattan",
            steps.GoToProbePoint is null
                ? "Lägg plattan på materialet och jogga fräsen över den, några mm ovanför. (Ingen tidigare Z-probning att åka tillbaka till.)"
                : "Lägg plattan där du probade förra gången. Knappen åker dit högt i luften – jogga sedan ned till några mm ovanför plattan.",
            _back));
        root.Children.Add(StepCard("4. Proba Z", "Z0 sätts på materialytan med den nya fräsen, och nästa program laddas och kontrolleras mot den. Ta bort plattan efteråt.", _probe));
        root.Children.Add(StepCard("5. Starta", manualSpindle ? "Programmet pausar och väntar på att du startar fräsen." : "Spindeln startas av programmet.", _start));
        root.Children.Add(_status);

        var close = new Button { Content = "Stäng", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = root;

        if (steps.GoToProbePoint is null) _back.Content = "Plattan och fräsen är på plats";
        _go.Click += async (_, _) => await Run(0, steps.GoToChange ?? (() => Task.FromResult<string?>(null)), steps.GoToChange is null ? "Z är uppe. Jogga dit det är lätt att byta." : $"Framme vid {steps.ChangePlace}.");
        _changed.Click += (_, _) => { Mark(1, true); _status.Text = "Fräsen bytt."; };
        _back.Click += async (_, _) => await Run(2, steps.GoToProbePoint ?? (() => Task.FromResult<string?>(null)),
            steps.GoToProbePoint is null ? "Klart för probning." : "Över probpunkten. Jogga ned till några mm ovanför plattan och klicka Proba Z.");
        _probe.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; _status.Foreground = Muted; _status.Text = "Probar Z …";
            try
            {
                var (ok, message) = await steps.ProbeZ();
                Mark(3, ok);
                _status.Foreground = ok ? Done : Bad;
                _status.Text = ok ? message + " Ta bort plattan." : message;
            }
            finally { _busy = false; }
        };
        _start.Click += (_, _) => { if (steps.Start()) { Mark(4, true); Close(); } };
    }

    private async Task Run(int index, Func<Task<string?>> action, string ok)
    {
        if (_busy) return;
        _busy = true; _status.Foreground = Muted; _status.Text = "Kör …";
        try
        {
            var error = await action();
            Mark(index, error is null);
            _status.Foreground = error is null ? Done : Bad;
            _status.Text = error ?? ok;
        }
        finally { _busy = false; }
    }

    private Border StepCard(string title, string text, Button button)
    {
        var mark = new TextBlock { Text = "○", FontSize = 16, Foreground = Muted, Width = 24, VerticalAlignment = VerticalAlignment.Top };
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 6, 0, 0);
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        body.Children.Add(button);
        var row = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark); row.Children.Add(body);
        var card = new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(0, 0, 0, 8), Child = row };
        _cards.Add((card, mark));
        return card;
    }

    private void Mark(int index, bool ok)
    {
        var (card, mark) = _cards[index];
        mark.Text = ok ? "✓" : "✗";
        mark.Foreground = ok ? Done : Bad;
        if (ok && index + 1 < _cards.Count) _cards[index + 1].Mark.Foreground = Active;
    }

    private static Button Btn(string text) => new() { Content = text, Padding = new Thickness(12, 4, 12, 4) };

    private static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}
