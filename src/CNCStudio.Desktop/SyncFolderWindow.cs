using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Arkiv → Synkmapp: chooses a folder that Google Drive for desktop (or OneDrive/Dropbox) keeps in
/// step between computers. Projects, the tool library and the default machine profile are stored there.
/// </summary>
public sealed class SyncFolderWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Good = B(0x4C, 0xE0, 0x7A), Warn = B(0xFF, 0xB0, 0x3B), Bad = B(0xFF, 0x7A, 0x86);
    private readonly AppStorage _storage;
    private readonly StackPanel _body = new();

    /// <summary>True if the sync folder was changed (the main window then reloads tools etc.).</summary>
    public bool Changed { get; private set; }

    public SyncFolderWindow(AppStorage storage)
    {
        _storage = storage;
        Title = "Synkmapp (Google Drive)";
        Width = 720; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new Border { Padding = new Thickness(22, 18, 22, 18), Child = _body };
        Build();
    }

    private void Build()
    {
        _body.Children.Clear();
        Text("SYNKMAPP – PROJEKT PÅ FLERA DATORER", 20, FontWeights.Bold, Ink, 0, 4);
        Text("Välj en mapp i Google Drive. Google Drive för datorn håller den likadan på alla datorer där du är inloggad, " +
             "så du kan designa på en dator och köra projektet från datorn vid maskinen.", 13, FontWeights.Normal, Soft, 0, 12);

        if (_storage.ActiveSyncFolder is string active)
            Text("✓  Synkas nu via " + active, 14, FontWeights.SemiBold, Good, 0, 4);
        else if (_storage.SyncFolderMissing)
            Text("!  Synkmappen hittas inte: " + _storage.Settings.SyncFolder + ". Är Google Drive igång och inloggat? " +
                 "Tills den syns igen sparas allt bara på den här datorn.", 14, FontWeights.SemiBold, Bad, 0, 4);
        else
            Text("Nu: allt sparas bara på den här datorn.", 14, FontWeights.SemiBold, Warn, 0, 4);

        var info = new Border { Background = B(0x1F, 0x2E, 0x3F), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 10, 0, 12) };
        var list = new StackPanel();
        list.Children.Add(Line("Delas mellan datorerna:", FontWeights.SemiBold, Ink));
        list.Children.Add(Line("•  Projekt (mappen Projekt) – Öppna och Spara börjar där", FontWeights.Normal, Soft));
        list.Children.Add(Line("•  Fräsbiblioteket (Inställningar\\tools.json)", FontWeights.Normal, Soft));
        list.Children.Add(Line("•  Standardmaskinprofilen för nya projekt (Inställningar\\maskinprofil.json)", FontWeights.Normal, Soft));
        list.Children.Add(Line("Stannar på varje dator (hör till datorn vid maskinen):", FontWeights.SemiBold, Ink, 8));
        list.Children.Add(Line("•  Avbrutna jobb, loggar och säkerhetskopior av $$", FontWeights.Normal, Soft));
        info.Child = list;
        _body.Children.Add(info);

        var found = AppStorage.FindGoogleDriveFolders();
        if (found.Count > 0)
        {
            Text("Google Drive hittades:", 13, FontWeights.SemiBold, Ink, 0, 4);
            foreach (var root in found)
            {
                var target = Path.Combine(root, "CNC Studio");
                var b = Btn($"Använd  {target}", true);
                b.Click += (_, _) => Choose(target);
                _body.Children.Add(b);
            }
        }
        else
            Text("Google Drive för datorn hittades inte på den här datorn. Installera det (google.com/drive/download), logga in " +
                 "med samma konto på båda datorerna och öppna det här fönstret igen – eller välj en mapp själv.", 13, FontWeights.Normal, Warn, 0, 6);

        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var pick = Btn("Välj annan mapp…", false);
        pick.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Välj synkmapp för CNC Studio", InitialDirectory = found.FirstOrDefault() ?? "" };
            if (dialog.ShowDialog(this) == true) Choose(dialog.FolderName);
        };
        row.Children.Add(pick);
        if (_storage.Settings.SyncFolder is not null)
        {
            var stop = Btn("Sluta synka (bara den här datorn)", false);
            stop.Click += (_, _) =>
            {
                if (AppDialog.Show(this, "Sluta använda synkmappen? Filerna där ligger kvar, men CNC Studio sparar sedan bara på den här datorn.",
                        "Synkmapp", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
                _storage.SetSyncFolder(null); Changed = true; Build();
            };
            row.Children.Add(stop);
        }
        var close = Btn("Stäng", false); close.IsCancel = true; close.Click += (_, _) => Close();
        row.Children.Add(close);
        _body.Children.Add(row);

        Text("Tips: ha inte samma projekt öppet på två datorer samtidigt. Sparar båda gör Google Drive en konfliktkopia av filen.", 12, FontWeights.Normal, Muted, 12, 0);
    }

    private void Choose(string folder)
    {
        if (AppDialog.Show(this, $"Använda {folder} som synkmapp?\n\n" +
                "• Mapparna Projekt och Inställningar skapas där.\n" +
                "• Den här datorns fräsbibliotek och maskinprofil kopieras dit om mappen inte redan har några (från en annan dator).\n" +
                "• Gör samma val på den andra datorn så delar de projekt och inställningar.",
                "Synkmapp", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var copied = _storage.SetSyncFolder(folder);
            Changed = true;
            Build();
            if (copied.Count > 0) Text("Kopierat dit: " + string.Join(", ", copied), 13, FontWeights.Normal, Good, 6, 0);
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, "Kunde inte använda mappen: " + ex.Message, "Synkmapp", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Text(string text, double size, FontWeight weight, Brush color, double top, double bottom) =>
        _body.Children.Add(new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, bottom) });

    private static TextBlock Line(string text, FontWeight weight, Brush color, double top = 0) =>
        new() { Text = text, FontWeight = weight, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 2), FontSize = 13 };

    private static Button Btn(string text, bool primary) => new()
    {
        Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left,
        Foreground = Brushes.White, Background = primary ? B(0x24, 0x61, 0x8C) : B(0x34, 0x47, 0x5A), BorderBrush = B(0x50, 0x67, 0x83)
    };

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
