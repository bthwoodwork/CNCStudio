using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Material library: the materials you cut (in categories). Cutting data per material is set on each
/// tool in the tool library; here you see which tools already have data for the selected material.
/// Edits a copy; <see cref="Result"/> is set only on Spara.
/// </summary>
public sealed class MaterialLibraryWindow : Window
{
    private static readonly SolidColorBrush Ink = B(0xE8, 0xF1, 0xFA), Soft = B(0xB9, 0xCB, 0xDD), Muted = B(0x8C, 0xA3, 0xBA),
        Field = B(0x1C, 0x2A, 0x39), Edge = B(0x50, 0x67, 0x83), Warn = B(0xFF, 0xB3, 0x8A);
    private readonly ObservableCollection<MaterialDefinition> _materials;
    private readonly ToolLibrary _tools;
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(MaterialDefinition.Name), Background = Field, Foreground = Ink, BorderBrush = Edge };
    private readonly TextBox _name = Box(), _notes = Box();
    private readonly ComboBox _category = new() { IsEditable = true, Margin = new Thickness(3) };
    private readonly TextBlock _usage = new() { TextWrapping = TextWrapping.Wrap, Foreground = Soft, Margin = new Thickness(3, 10, 3, 3) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Foreground = Warn, Margin = new Thickness(3) };
    private readonly StackPanel _editor = new();

    public MaterialLibrary? Result { get; private set; }

    public MaterialLibraryWindow(MaterialLibrary library, ToolLibrary tools, string? path = null)
    {
        _tools = tools;
        _materials = new ObservableCollection<MaterialDefinition>(library.Materials.Select(m => m.Clone()));
        Title = "Materialbibliotek";
        Width = 860; Height = 600; MinWidth = 700; MinHeight = 460;
        Background = B(0x17, 0x21, 0x2C); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;

        var view = CollectionViewSource.GetDefaultView(_materials);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MaterialDefinition.Category)));
        view.SortDescriptions.Add(new SortDescription(nameof(MaterialDefinition.Category), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(MaterialDefinition.Name), ListSortDirection.Ascending));
        _list.ItemsSource = view;
        var header = new FrameworkElementFactory(typeof(TextBlock));
        header.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        header.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        header.SetValue(TextBlock.FontSizeProperty, 11.0);
        header.SetValue(TextBlock.ForegroundProperty, B(0xA5, 0xC0, 0xD9));
        header.SetValue(TextBlock.MarginProperty, new Thickness(4, 8, 4, 2));
        _list.GroupStyle.Add(new GroupStyle { HeaderTemplate = new DataTemplate { VisualTree = header } });
        _list.SelectionChanged += (_, _) => Load();

        var top = new StackPanel { Margin = new Thickness(3, 0, 3, 10), Children =
        {
            new TextBlock { Text = "MATERIALBIBLIOTEK", FontSize = 18, FontWeight = FontWeights.Bold },
            new TextBlock { Text = "Materialen du fräser i, i kategorier. Välj materialet för projektet under Projekt och material – då hämtar CAM skärdatan " +
                                   "för det materialet från verktygsbiblioteket (fräsens egna värden per material).", TextWrapping = TextWrapping.Wrap, Foreground = Soft },
            new TextBlock { Text = path is null ? "" : "Sparas i " + path, FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0) }
        } };

        var buttons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        buttons.Children.Add(Button("Nytt", (_, _) => New()));
        buttons.Children.Add(Button("Kopiera", (_, _) => Copy()));
        buttons.Children.Add(Button("Ta bort", (_, _) => Delete()));
        var left = new DockPanel { Width = 280 };
        DockPanel.SetDock(buttons, Dock.Bottom); left.Children.Add(buttons); left.Children.Add(_list);

        _editor.Children.Add(Label("Namn")); _editor.Children.Add(_name);
        _editor.Children.Add(Label("Kategori")); _editor.Children.Add(_category);
        _editor.Children.Add(Label("Anteckning (t.ex. leverantör, densitet, fiberriktning)")); _editor.Children.Add(_notes);
        _editor.Children.Add(Button("Uppdatera materialet", (_, _) => { if (Apply()) _message.Text = "Uppdaterat – klicka Spara."; }, primary: true));
        _editor.Children.Add(_usage);
        _editor.Children.Add(_message);

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left); body.Children.Add(left);
        body.Children.Add(new Border { Margin = new Thickness(14, 0, 0, 0), Child = _editor });

        var save = Button("Spara", (_, _) => Save(), primary: true); save.IsDefault = true;
        var cancel = Button("Avbryt", (_, _) => Close()); cancel.IsCancel = true;
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { save, cancel } };

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;
        RefreshCategories();
        Select(_materials.OrderBy(m => m.Category).ThenBy(m => m.Name).FirstOrDefault());
    }

    private MaterialDefinition? Current => _list.SelectedItem as MaterialDefinition;

    private void Select(MaterialDefinition? m)
    {
        CollectionViewSource.GetDefaultView(_materials).Refresh();
        _list.SelectedItem = m;
        _editor.IsEnabled = m is not null;
        Load();
    }

    private void RefreshCategories()
    {
        var text = _category.Text;
        _category.ItemsSource = _materials.Select(m => m.Category).Concat(new[] { "Trä", "Skivmaterial", "Plast", "Metall", "Övrigt" })
            .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
        _category.Text = text;
    }

    private void Load()
    {
        var m = Current;
        _editor.IsEnabled = m is not null;
        if (m is null) return;
        _name.Text = m.Name; _category.Text = m.Category; _notes.Text = m.Notes; _message.Text = "";
        var withData = _tools.Tools.Where(t => t.CuttingData.Any(c => c.MaterialId == m.Id)).Select(t => t.Name).ToList();
        _usage.Text = withData.Count == 0
            ? "Ingen fräs har egen skärdata för det här materialet än – fräsarnas standardvärden används. Lägg till skärdata i Verktygsbiblioteket."
            : $"Fräsar med skärdata för {m.Name}: {string.Join(", ", withData)}.";
    }

    private bool Apply()
    {
        var m = Current;
        if (m is null) return true;
        var name = _name.Text.Trim();
        if (name.Length == 0) { _message.Text = "Ange ett namn på materialet."; return false; }
        if (_materials.Any(o => o != m && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { _message.Text = "Det finns redan ett material med det namnet."; return false; }
        m.Name = name; m.Category = string.IsNullOrWhiteSpace(_category.Text) ? "Övrigt" : _category.Text.Trim(); m.Notes = _notes.Text;
        RefreshCategories();
        Select(m);
        return true;
    }

    private void New()
    {
        if (!Apply()) return;
        var m = new MaterialDefinition { Name = Unique("Nytt material"), Category = Current?.Category ?? "Trä" };
        _materials.Add(m);
        Select(m);
        _name.Focus(); _name.SelectAll();
    }

    private void Copy()
    {
        if (Current is null || !Apply()) return;
        var m = Current!.Clone();
        m.Id = Guid.NewGuid(); m.Name = Unique(m.Name + " (kopia)");
        _materials.Add(m);
        Select(m);
    }

    private void Delete()
    {
        var m = Current;
        if (m is null) return;
        int tools = _tools.Tools.Count(t => t.CuttingData.Any(c => c.MaterialId == m.Id));
        if (AppDialog.Show(this, $"Ta bort \"{m.Name}\"?" + (tools > 0 ? $"\n\n{tools} fräsar har skärdata för materialet. Den sparas kvar i verktygsbiblioteket och visas där som \"finns inte i materialbiblioteket\"." : ""),
                "Ta bort material", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _materials.Remove(m);
        Select(_materials.FirstOrDefault());
    }

    private void Save()
    {
        if (!Apply()) return;
        Result = new MaterialLibrary { Materials = _materials.ToList() };
        DialogResult = true;
    }

    private string Unique(string name)
    {
        var candidate = name; int i = 2;
        while (_materials.Any(m => m.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) candidate = $"{name} {i++}";
        return candidate;
    }

    private static TextBox Box() => new() { Margin = new Thickness(3), Padding = new Thickness(5), Foreground = B(0x15, 0x23, 0x32), Background = B(0xF5, 0xF8, 0xFB) };
    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(3, 8, 3, 0) };

    private static Button Button(string text, RoutedEventHandler click, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(12, 5, 12, 5), Foreground = Brushes.White,
            Background = primary ? B(0x24, 0x61, 0x8C) : B(0x34, 0x47, 0x5A), BorderBrush = B(0x50, 0x67, 0x83), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += click;
        return b;
    }

    private static SolidColorBrush B(byte r, byte g, byte b) { var s = new SolidColorBrush(Color.FromRgb(r, g, b)); s.Freeze(); return s; }
}
