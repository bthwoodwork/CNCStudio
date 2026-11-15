using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core;
using CNCStudio.Core.Grbl;

namespace CNCStudio.Desktop;

/// <summary>Maskin → Platser…: the machine's saved places – save where the tool is, edit, remove, or start from the suggestions.</summary>
public sealed class LocationsWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Muted = B(0x8C, 0xA3, 0xBA), Bad = B(0xFF, 0x7A, 0x86);

    public SavedLocations Result { get; }
    private readonly MachineProfile _limits;
    private readonly Func<double[]?> _position;
    private readonly ListBox _list = new() { Height = 220, Background = B(0x10, 0x20, 0x2E), Foreground = Ink, BorderBrush = B(0x3C, 0x51, 0x67) };
    private readonly TextBox _name = Box(), _x = Box(), _y = Box(), _z = Box();
    private readonly CheckBox _keepUp = new() { Content = "Z stannar uppe (vanligast för fräsbyte och parkering)", Foreground = Ink, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _error = new() { Foreground = Bad, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    public LocationsWindow(SavedLocations locations, MachineProfile limits, string machineName, Func<double[]?> position)
    {
        Result = new SavedLocations { Locations = locations.Locations.Select(l => new SavedLocation { Id = l.Id, Name = l.Name, X = l.X, Y = l.Y, Z = l.Z }).ToList() };
        _limits = limits; _position = position;
        Title = "Platser – " + machineName;
        Width = 560; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        body.Children.Add(new TextBlock
        {
            Text = "Platser maskinen åker till med ett klick, i maskinkoordinater (gäller den här maskinen, inte projektet). Z lyfts alltid först. " +
                   "Enklast: jogga dit, skriv ett namn och klicka Spara där fräsen står.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        body.Children.Add(_list);
        var listButtons = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        var up = new Button { Content = "↑" }; var down = new Button { Content = "↓" }; var remove = new Button { Content = "Ta bort" };
        var suggest = new Button { Content = "Lägg till förslag (fräsbyte fram, parkering bak)" };
        listButtons.Children.Add(up); listButtons.Children.Add(down); listButtons.Children.Add(remove); listButtons.Children.Add(suggest);
        body.Children.Add(listButtons);

        body.Children.Add(new TextBlock { Text = "PLATS", Foreground = Muted, FontSize = 11, FontWeight = FontWeights.Bold });
        body.Children.Add(Row("Namn", _name));
        var xyz = new UniformGrid3(Row("X (MPos)", _x), Row("Y (MPos)", _y), Row("Z (MPos)", _z));
        body.Children.Add(xyz);
        body.Children.Add(_keepUp);
        var here = new Button { Content = "Spara där fräsen står", FontWeight = FontWeights.SemiBold };
        var add = new Button { Content = "Lägg till med värdena" };
        var update = new Button { Content = "Uppdatera markerad" };
        body.Children.Add(new WrapPanel { Margin = new Thickness(0, 8, 0, 0), Children = { here, add, update } });
        body.Children.Add(_error);

        var ok = new Button { Content = "Spara", IsDefault = true, FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Avbryt", IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;
        var root = new DockPanel();
        var bottom = new WrapPanel { Margin = new Thickness(18, 0, 18, 14), Children = { ok, cancel } };
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = root;

        _keepUp.Click += (_, _) => _z.IsEnabled = _keepUp.IsChecked != true;
        _keepUp.IsChecked = true; _z.IsEnabled = false;
        _list.SelectionChanged += (_, _) => Show(Selected);
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        remove.Click += (_, _) => { if (Selected is { } s) { Result.Locations.Remove(s); Fill(); } };
        suggest.Click += (_, _) =>
        {
            foreach (var s in SavedLocations.Suggestions(_limits).Where(s => Result.Locations.All(l => l.Name != s.Name))) Result.Locations.Add(s);
            Fill();
        };
        here.Click += (_, _) =>
        {
            if (_position() is not { Length: >= 3 } p) { _error.Text = "Fräsens position är okänd – anslut och homa maskinen."; return; }
            var loc = new SavedLocation { Name = _name.Text.Trim(), X = Math.Round(p[0], 3), Y = Math.Round(p[1], 3), Z = _keepUp.IsChecked == true ? null : Math.Round(p[2], 3) };
            if (Validate(loc)) { Result.Locations.Add(loc); Fill(); _list.SelectedItem = Item(loc); }
        };
        add.Click += (_, _) => { if (Read(Guid.NewGuid()) is { } loc && Validate(loc)) { Result.Locations.Add(loc); Fill(); } };
        update.Click += (_, _) =>
        {
            if (Selected is not { } s) { _error.Text = "Markera en plats i listan."; return; }
            if (Read(s.Id) is not { } loc || !Validate(loc)) return;
            Result.Locations[Result.Locations.IndexOf(s)] = loc; Fill();
        };
        Fill();
    }

    private SavedLocation? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as SavedLocation;
    private ListBoxItem? Item(SavedLocation l) => _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == l);

    private void Fill()
    {
        _list.Items.Clear();
        foreach (var l in Result.Locations)
            _list.Items.Add(new ListBoxItem
            {
                Tag = l,
                Content = $"{l.Name}   X {F(l.X)}  Y {F(l.Y)}  " + (l.Z is double z ? $"Z {F(z)}" : "Z uppe") +
                          (SavedLocations.Check(l, _limits) is string p ? "   ⚠ " + p : ""),
            });
    }

    private void Show(SavedLocation? l)
    {
        if (l is null) return;
        _name.Text = l.Name; _x.Text = F(l.X); _y.Text = F(l.Y); _z.Text = l.Z is double z ? F(z) : "";
        _keepUp.IsChecked = l.Z is null; _z.IsEnabled = l.Z is not null;
        _error.Text = "";
    }

    private SavedLocation? Read(Guid id)
    {
        bool P(TextBox t, out double v) => double.TryParse(t.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        if (!P(_x, out var x) || !P(_y, out var y)) { _error.Text = "X och Y måste vara tal (maskinkoordinater, negativa)."; return null; }
        double? z = null;
        if (_keepUp.IsChecked != true) { if (!P(_z, out var zz)) { _error.Text = "Z måste vara ett tal, eller kryssa i att Z stannar uppe."; return null; } z = zz; }
        return new SavedLocation { Id = id, Name = _name.Text.Trim(), X = x, Y = y, Z = z };
    }

    private bool Validate(SavedLocation l)
    {
        _error.Text = SavedLocations.Check(l, _limits) ?? "";
        return _error.Text.Length == 0;
    }

    private void Move(int by)
    {
        if (Selected is not { } s) return;
        int i = Result.Locations.IndexOf(s), j = i + by;
        if (j < 0 || j >= Result.Locations.Count) return;
        Result.Locations.RemoveAt(i); Result.Locations.Insert(j, s);
        Fill(); _list.SelectedItem = Item(s);
    }

    private static FrameworkElement Row(string label, Control c) =>
        new StackPanel { Margin = new Thickness(0, 2, 6, 2), Children = { new TextBlock { Text = label, Foreground = Muted, FontSize = 11 }, c } };
    private static TextBox Box() => new() { Padding = new Thickness(5, 3, 5, 3) };
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }

    private sealed class UniformGrid3 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid3(params UIElement[] children) { Columns = 3; foreach (var c in children) Children.Add(c); }
    }
}
