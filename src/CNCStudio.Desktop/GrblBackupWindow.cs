using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>
/// Maskiner → (a machine) → Säkerhetskopior av $$: save a copy, see that machine's copies, compare one with
/// the machine and restore the settings that differ (written one by one and read back). A copy is also made
/// automatically before every change of settings from CNC Studio, so any change can be undone here.
/// Comparing, saving and restoring need that machine to be the connected one.
/// </summary>
public sealed class GrblBackupWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Card = B(0x1C, 0x2A, 0x39), Changed = B(0x5A, 0x40, 0x1E), Good = B(0x7C, 0xD9, 0x9C), Bad = B(0xFF, 0x7A, 0x86);

    private readonly GrblClient? _grbl;
    private readonly string? _firmware;
    private readonly MachineProfile _profile;
    private readonly ListBox _list = new() { Background = B(0x10, 0x1B, 0x27), Foreground = Ink, BorderBrush = B(0x3C, 0x51, 0x67) };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _heading = new() { FontSize = 15, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Soft, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _note = new() { Padding = new Thickness(5, 3, 5, 3), MinWidth = 260, ToolTip = "Valfri anteckning, t.ex. \"Före kalibrering av steg/mm\"." };
    private readonly Button _save = new() { Content = "Spara kopia nu" };
    private readonly Button _restore = new() { Content = "Återställ markerade…", IsEnabled = false };
    private readonly CheckBox _onlyDiff = new() { Content = "Visa bara skillnader", IsChecked = true, Foreground = Ink, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<int, CheckBox> _picks = new();
    private IReadOnlyDictionary<int, double>? _machine;
    private GrblBackupFile? _shown;

    /// <param name="grbl">The connection, only when <paramref name="machine"/> is the connected machine.</param>
    public GrblBackupWindow(MachineProfile machine, GrblClient? grbl, string? firmware)
    {
        _profile = machine; _grbl = grbl; _firmware = firmware;
        Title = $"Säkerhetskopior av $$ – {machine.Name}";
        Width = 1000; Height = 720; MinWidth = 760; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        var open = new Button { Content = "Öppna mappen" };
        var close = new Button { Content = "Stäng", IsCancel = true };
        _save.Click += async (_, _) => await SaveNow();
        _restore.Click += async (_, _) => await Restore();
        open.Click += (_, _) =>
        {
            var folder = GrblBackup.MachineFolder(AppStorage.Current.GrblBackupFolder, _profile);
            Directory.CreateDirectory(folder); System.Diagnostics.Process.Start("explorer.exe", folder);
        };
        close.Click += (_, _) => Close();
        foreach (var b in new[] { _save, _restore, open, close })
        {
            b.Padding = new Thickness(12, 5, 12, 5); b.Margin = new Thickness(0, 0, 8, 0); b.Foreground = Brushes.White;
            b.Background = b == _save ? B(0x24, 0x61, 0x8C) : b == _restore ? B(0x8A, 0x5A, 0x1E) : B(0x34, 0x47, 0x5A); b.BorderBrush = B(0x50, 0x67, 0x83);
        }
        _list.SelectionChanged += (_, _) => Show(_list.SelectedItem is ListBoxItem { Tag: GrblBackupFile f } ? f : null);

        var header = new StackPanel { Margin = new Thickness(16, 12, 16, 8), Children =
        {
            new TextBlock { Text = "SÄKERHETSKOPIOR AV $$ – " + machine.Name.ToUpperInvariant(), FontSize = 18, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Spara maskinens inställningar och återställ dem om något blir fel. En kopia sparas också automatiskt före varje ändring från CNC Studio. " +
                                   $"Här visas bara kopior av {machine.Name}. De ligger i CNC Studio\\Inställningar\\GRBL-kopior\\{Path.GetFileName(GrblBackup.MachineFolder("", machine))} och är vanlig text ($n=värde).",
                Foreground = Soft, TextWrapping = TextWrapping.Wrap },
            new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { new TextBlock { Text = "Anteckning:", Foreground = Soft, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, _note, new Border { Width = 8 }, _save, open } },
            _status
        } };
        var left = new DockPanel { Width = 300, Margin = new Thickness(16, 0, 10, 0) };
        var lt = new TextBlock { Text = "KOPIOR (senaste först)", Foreground = Muted, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(lt, Dock.Top); left.Children.Add(lt); left.Children.Add(_list);
        var right = new DockPanel();
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { _restore, _onlyDiff } };
        _onlyDiff.Click += (_, _) => Show(_shown);
        DockPanel.SetDock(_heading, Dock.Top); right.Children.Add(_heading);
        DockPanel.SetDock(actions, Dock.Bottom); right.Children.Add(actions);
        right.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(5), Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows } });
        var middle = new DockPanel { Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(left, Dock.Left); middle.Children.Add(left); middle.Children.Add(right);
        var footer = new WrapPanel { Margin = new Thickness(16, 10, 16, 14), Children = { close } };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(middle);
        Content = root;

        _save.IsEnabled = _grbl is not null;
        Loaded += async (_, _) => { await ReadMachine(); LoadList(null); };
    }

    private static IEnumerable<string> Folders()
    {
        yield return AppStorage.Current.GrblBackupFolder;        // copies from before one folder per machine
        // Each machine keeps its copies in a subfolder.
        if (Directory.Exists(AppStorage.Current.GrblBackupFolder)) foreach (var d in Directory.GetDirectories(AppStorage.Current.GrblBackupFolder)) yield return d;
        // Copies the first test made, on every computer.
        var root = AppStorage.Current.LogsRoot;
        if (Directory.Exists(root)) foreach (var d in Directory.GetDirectories(root)) yield return d;
    }

    private void LoadList(string? select)
    {
        _list.Items.Clear();
        foreach (var b in GrblBackup.ForMachine(GrblBackup.List(Folders()), AppStorage.Current.GrblBackupFolder, _profile))
        {
            string where = b.Path.Contains(AppStorage.LogsFolderName) ? " · från Första test" : "";
            string unknown = GrblBackup.IsUnassigned(b) && !MachineLibrary.IsDemo(_profile) ? " · äldre kopia, maskin okänd" : "";
            var item = new ListBoxItem { Tag = b, Padding = new Thickness(6, 4, 6, 4), Content = new StackPanel { Children =
            {
                new TextBlock { Text = b.Title, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ink },
                new TextBlock { Text = (b.Computer ?? "") + where + unknown, Foreground = Muted, FontSize = 11 }
            } } };
            _list.Items.Add(item);
        }
        if (_list.Items.Count == 0) { _heading.Text = "Inga kopior ännu."; _rows.Children.Clear(); return; }
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((GrblBackupFile)i.Tag).Path == select) ?? _list.Items[0];
    }

    private async Task ReadMachine()
    {
        if (_grbl is null) { _status.Text = $"{_profile.Name} är inte ansluten – kopiorna kan visas men inte jämföras eller återställas."; return; }
        try { _machine = await _grbl.ReadSettingsAsync(); _status.Text = $"Maskinens inställningar lästa ({_machine.Count} st)."; }
        catch (Exception ex) { _machine = null; _status.Text = "Kunde inte läsa maskinens inställningar: " + ex.Message; }
    }

    private void Show(GrblBackupFile? f)
    {
        _shown = f; _rows.Children.Clear(); _picks.Clear();
        if (f is null) return;
        var diffs = _machine is null ? Array.Empty<GrblSettingDifference>() : GrblBackup.Compare(f.Settings, _machine);
        var changed = diffs.Select(d => d.Number).ToHashSet();
        _heading.Text = f.Title + (_machine is null ? "" : diffs.Count == 0 ? "  –  stämmer med maskinen" : $"  –  {diffs.Count} skiljer från maskinen");
        _heading.Foreground = _machine is null ? Ink : diffs.Count == 0 ? Good : B(0xFF, 0xD3, 0x9F);

        var grid = new Grid();
        foreach (var w in new[] { 34.0, 64, double.NaN, 110, 110 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        int row = 0;
        void Cell(UIElement e, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); grid.Children.Add(e); }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var (text, col) in new[] { ("", 0), ("NR", 1), ("INSTÄLLNING", 2), ("I KOPIAN", 3), ("I MASKINEN", 4) })
            Cell(new TextBlock { Text = text, Foreground = Muted, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(4, 0, 4, 4) }, col);
        bool onlyDiff = _onlyDiff.IsChecked == true && changed.Count > 0;
        foreach (var n in f.Settings.Keys.Union(_machine?.Keys ?? Enumerable.Empty<int>()).Where(k => !onlyDiff || changed.Contains(k)).OrderBy(k => k))
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bool diff = changed.Contains(n);
            if (diff)
            {
                var bg = new Border { Background = Changed, CornerRadius = new CornerRadius(3) };
                Grid.SetColumnSpan(bg, 5); Cell(bg, 0);
            }
            bool restorable = diff && f.Settings.ContainsKey(n) && _machine?.ContainsKey(n) == true;
            if (restorable) { var cb = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; _picks[n] = cb; Cell(cb, 0); }
            var info = GrblSettings.Catalog.TryGetValue(n, out var i) ? i : null;
            Cell(new TextBlock { Text = "$" + n, Foreground = Ink, Margin = new Thickness(4, 3, 4, 3) }, 1);
            Cell(new TextBlock { Text = (info?.Name ?? "") + (string.IsNullOrEmpty(info?.Unit) ? "" : $"  ({info!.Unit})"), Foreground = Soft, Margin = new Thickness(4, 3, 4, 3), TextTrimming = TextTrimming.CharacterEllipsis }, 2);
            Cell(new TextBlock { Text = f.Settings.TryGetValue(n, out var a) ? GrblBackup.Value(n, a) : "–", Foreground = Ink, Margin = new Thickness(4, 3, 4, 3), FontFamily = new FontFamily("Consolas") }, 3);
            Cell(new TextBlock { Text = _machine is null ? "" : _machine.TryGetValue(n, out var m) ? GrblBackup.Value(n, m) : "–", Foreground = diff ? B(0xFF, 0xD3, 0x9F) : Ink, Margin = new Thickness(4, 3, 4, 3), FontFamily = new FontFamily("Consolas") }, 4);
        }
        _rows.Children.Add(grid);
        _restore.IsEnabled = _grbl is not null && _picks.Count > 0;
    }

    private async Task SaveNow()
    {
        if (_grbl is null) return;
        _save.IsEnabled = false;
        try
        {
            var settings = await _grbl.ReadSettingsAsync();
            var note = _note.Text.Trim();
            var path = GrblBackup.Save(GrblBackup.MachineFolder(AppStorage.Current.GrblBackupFolder, _profile), settings, DateTime.Now,
                AppStorage.ComputerName, _firmware, note.Length == 0 ? "Sparad för hand" : note, _profile);
            _machine = settings;
            _status.Foreground = Good; _status.Text = $"Kopia sparad: {Path.GetFileName(path)} ({settings.Count} inställningar).";
            _note.Text = "";
            LoadList(path);
        }
        catch (Exception ex) { _status.Foreground = Bad; _status.Text = "Kunde inte spara kopian: " + ex.Message; }
        finally { _save.IsEnabled = true; }
    }

    private async Task Restore()
    {
        if (_grbl is null || _shown is null || _machine is null) return;
        var diffs = GrblBackup.Compare(_shown.Settings, _machine).Where(d => _picks.TryGetValue(d.Number, out var cb) && cb.IsChecked == true).ToList();
        var changes = GrblBackup.RestoreChanges(diffs);
        if (changes.Count == 0) { _status.Text = "Inga inställningar markerade."; return; }
        var text = string.Join("\n", diffs.Select(d => $"  ${d.Number} {d.Name}: {GrblBackup.Value(d.Number, d.InMachine!.Value)} → {GrblBackup.Value(d.Number, d.InCopy!.Value)}"));
        bool risky = diffs.Any(d => d.Number is 3 or 23 or 100 or 101 or 102 or 130 or 131 or 132 or 20 or 21 or 22);
        if (AppDialog.Show(this, $"Skriva {changes.Count} inställning(ar) från kopian till maskinen?\n\n{text}\n\n" +
                "Värdena skrivs en i taget och kontrolläses. Nuvarande inställningar sparas först som en ny kopia." +
                (risky ? "\n\nObs: riktningar, steg/mm, arbetsområde eller gränslägen ändras – kör homing efteråt och jogga försiktigt." : ""),
                "Återställ inställningar", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _restore.IsEnabled = false;
        try
        {
            var r = await _grbl.WriteSettingsAsync(changes);
            _status.Foreground = r.Success ? Good : Bad;
            _status.Text = r.Success ? "Återställt: " + r.Message : "Återställningen misslyckades: " + r.Message;
            await ReadMachine();
            LoadList(_shown.Path);
        }
        catch (Exception ex) { _status.Foreground = Bad; _status.Text = "Återställningen avbröts: " + ex.Message; }
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
