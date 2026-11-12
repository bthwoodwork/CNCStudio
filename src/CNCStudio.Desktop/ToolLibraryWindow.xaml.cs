using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// The tool library: tools in categories, their numbers, measurements and purchase details, default
/// cutting data and cutting data per material from the material library. Every change (Spara fräsen,
/// a new, copied or removed tool, a category change) is saved to the library at once through the save
/// callback, so the window can stay open while several tools are entered. Closing with unsaved changes
/// in the fields asks first.
/// </summary>
public partial class ToolLibraryWindow : Window
{
    private static readonly JsonSerializerOptions CompareJson = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly ObservableCollection<ToolDefinition> _tools;
    private List<string> _categories;
    private readonly MaterialLibrary _materials;
    private readonly Func<ToolLibrary, string?> _save;
    private readonly ObservableCollection<CuttingRow> _rows = new();
    private readonly ListCollectionView _view;
    private ToolDefinition? _shown;      // the tool the fields show
    private bool _loading, _reselecting;

    /// <summary>One material in the cutting-data table; empty cells = use the tool's defaults.</summary>
    public sealed class CuttingRow
    {
        public Guid MaterialId { get; init; }
        public string Material { get; init; } = "";
        public string MaterialName { get; init; } = "";
        public string Feed { get; set; } = "";
        public string Plunge { get; set; } = "";
        public string Pass { get; set; } = "";
        public string Rpm { get; set; } = "";
        public bool IsEmpty => new[] { Feed, Plunge, Pass, Rpm }.All(string.IsNullOrWhiteSpace);
    }

    /// <param name="save">Saves the whole library; returns an error text, or null when it is saved.</param>
    public ToolLibraryWindow(ToolLibrary library, MaterialLibrary materials, string? path, Func<ToolLibrary, string?> save)
    {
        InitializeComponent();
        _materials = materials;
        _save = save;
        var copy = library.Clone();
        _tools = new ObservableCollection<ToolDefinition>(copy.Tools);
        _categories = copy.Categories;
        PathText.Text = path is null ? "" : "Sparas i " + path;
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_tools);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ToolDefinition.CategoryOrDefault)));
        _view.CustomSort = new ToolOrder(this);
        ToolList.ItemsSource = _view;
        CuttingGrid.ItemsSource = _rows;
        CutterMaterialBox.ItemsSource = new[] { "Hårdmetall", "Hårdmetall, belagd", "HSS", "Diamant (PKD)" };
        RefreshCategories();
        var dup = Library().DuplicateToolNumbers();
        Select(_view.Cast<ToolDefinition>().FirstOrDefault());
        if (dup.Count > 0)
            MessageText.Text = "Samma verktygsnummer på flera fräsar: " + string.Join("; ", dup.Select(d => $"T{d.Number} ({string.Join(", ", d.Tools)})"));
    }

    /// <summary>Sorts the list by the library's category order, then diameter and name.</summary>
    private sealed class ToolOrder(ToolLibraryWindow w) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not ToolDefinition a || y is not ToolDefinition b) return 0;
            var lib = w.Library();
            int c = lib.CategoryOrder(a.CategoryOrDefault).CompareTo(lib.CategoryOrder(b.CategoryOrDefault));
            if (c != 0) return c;
            c = a.Diameter.CompareTo(b.Diameter);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    /// <summary>The library as it is in the window (the saved state plus nothing unsaved from the fields).</summary>
    private ToolLibrary Library() => new() { Tools = _tools.ToList(), Categories = _categories.ToList() };

    private ToolDefinition? Current => ToolList.SelectedItem as ToolDefinition;

    private void Select(ToolDefinition? tool)
    {
        _reselecting = true;
        try { _view.Refresh(); ToolList.SelectedItem = tool; }
        finally { _reselecting = false; }
        Show(tool);
    }

    private void RefreshCategories()
    {
        var text = CategoryBox.Text;
        CategoryBox.ItemsSource = Library().AllCategories()
            .Concat(new[] { "Raka fräsar", "Kulfräsar", "V-fräsar", "Kompressionsfräsar", "Planfräsar" })
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        CategoryBox.Text = text;
    }

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reselecting || _loading) return;
        if (Current == _shown) return;
        // Leaving a tool with unsaved changes: save, drop or stay.
        if (!ConfirmLeave())
        {
            _reselecting = true;
            try { ToolList.SelectedItem = _shown; } finally { _reselecting = false; }
            return;
        }
        Show(Current);
    }

    /// <summary>Fills the fields from <paramref name="t"/>.</summary>
    private void Show(ToolDefinition? t)
    {
        _shown = t;
        Editor.IsEnabled = SaveToolButton.IsEnabled = t is not null;
        if (t is null) return;
        _loading = true;
        NameBox.Text = t.Name;
        NumberBox.Text = t.ToolNumber?.ToString(CultureInfo.InvariantCulture) ?? "";
        CategoryBox.Text = t.CategoryOrDefault;
        KindBox.SelectedIndex = (int)t.Kind;
        DiameterBox.Text = F(t.Diameter); AngleBox.Text = F(t.AngleDeg); TipBox.Text = F(t.TipDiameter);
        FlutesBox.Text = t.Flutes.ToString(CultureInfo.InvariantCulture);
        CutterMaterialBox.Text = t.CutterMaterial;
        NotesBox.Text = t.Notes;
        ShankBox.Text = F(t.ShankDiameter); OverallBox.Text = F(t.OverallLength); FluteBox.Text = F(t.FluteLength); StickoutBox.Text = F(t.StickoutLength);
        VendorBox.Text = t.Vendor; ArticleBox.Text = t.ArticleNumber; LinkBox.Text = t.ProductLink;
        FeedBox.Text = F(t.FeedRate); PlungeBox.Text = F(t.PlungeRate);
        PassBox.Text = F(t.DepthPerPass); RpmBox.Text = F(t.SpindleRpm);
        _rows.Clear();
        foreach (var m in _materials.Materials.OrderBy(m => m.Category).ThenBy(m => m.Name))
            _rows.Add(Row(m.Id, m.DisplayName, m.Name, t.CuttingData.FirstOrDefault(c => c.MaterialId == m.Id)));
        // Cutting data for materials that are no longer in the library is kept and shown.
        foreach (var c in t.CuttingData.Where(c => _materials.Find(c.MaterialId) is null))
            _rows.Add(Row(c.MaterialId, c.MaterialName + " (finns inte i materialbiblioteket)", c.MaterialName, c));
        MessageText.Foreground = Warn;
        MessageText.Text = t.Warning ?? (_materials.Materials.Count == 0 ? "Inga material ännu – lägg upp dem i Materialbiblioteket." : "");
        _loading = false;
        UpdateKindFields();
    }

    private static CuttingRow Row(Guid id, string label, string name, MaterialCuttingData? c) => new()
    {
        MaterialId = id, Material = label, MaterialName = name,
        Feed = c is null ? "" : F(c.FeedRate), Plunge = c is null ? "" : F(c.PlungeRate),
        Pass = c is null ? "" : F(c.DepthPerPass), Rpm = c is null ? "" : F(c.SpindleRpm)
    };

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdateKindFields(); }

    private void UpdateKindFields()
    {
        if (AngleBox is null) return;
        var v = KindBox.SelectedIndex == (int)ToolKind.VBit ? Visibility.Visible : Visibility.Collapsed;
        AngleBox.Visibility = AngleLabel.Visibility = TipBox.Visibility = TipLabel.Visibility = v;
    }

    /// <summary>The shown tool with the fields' values, or null (and <paramref name="error"/>) when a value is invalid.</summary>
    private ToolDefinition? FromFields(out string? error)
    {
        error = null;
        var t = _shown;
        if (t is null) return null;
        CuttingGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!Num(DiameterBox, out var d) || !Num(FeedBox, out var f) || !Num(PlungeBox, out var p) ||
            !Num(PassBox, out var pass) || !Num(RpmBox, out var rpm) ||
            !int.TryParse(FlutesBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var flutes))
        { error = "Ange giltiga tal i diameter, antal skär och standardvärdena."; return null; }
        double angle = t.AngleDeg;
        bool vbit = KindBox.SelectedIndex == (int)ToolKind.VBit;
        if (vbit && !Num(AngleBox, out angle)) { error = "Ange V-fräsens vinkel."; return null; }
        int? number = null;
        if (NumberBox.Text.Trim() is { Length: > 0 } nt)
        {
            var digits = nt.TrimStart('T', 't').Trim();
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) { error = "Verktygsnumret ska vara ett heltal, t.ex. 3 eller T3 (eller tomt)."; return null; }
            number = n;
        }
        if (!Optional(ShankBox, "skaftdiametern", out var shank, out error) || !Optional(OverallBox, "totala längden", out var overall, out error) ||
            !Optional(FluteBox, "skärlängden", out var flute, out error) || !Optional(StickoutBox, "utsticket", out var stickout, out error) ||
            !Optional(TipBox, "spetsdiametern", out var tip, out error)) return null;

        var cutting = new List<MaterialCuttingData>();
        foreach (var r in _rows)
        {
            if (r.IsEmpty) continue;
            if (!Num(r.Feed, out var cf) || !Num(r.Plunge, out var cp) || !Num(r.Pass, out var cpass) || !Num(r.Rpm, out var crpm))
            { error = $"{r.MaterialName}: fyll i alla fyra värdena med tal, eller töm raden."; return null; }
            cutting.Add(new MaterialCuttingData { MaterialId = r.MaterialId, MaterialName = r.MaterialName, FeedRate = cf, PlungeRate = cp, DepthPerPass = cpass, SpindleRpm = crpm });
        }

        var c = t.Clone();
        c.Name = NameBox.Text.Trim(); c.Kind = (ToolKind)Math.Max(0, KindBox.SelectedIndex);
        c.ToolNumber = number;
        // Keep "no category" when the box still shows the type's default, so the tool follows its type.
        var category = CategoryBox.Text.Trim();
        c.Category = string.IsNullOrWhiteSpace(t.Category) && (category == t.CategoryOrDefault || category == ToolDefinition.DefaultCategory(c.Kind))
            ? "" : category;
        c.Diameter = d; c.AngleDeg = angle; c.Flutes = flutes;
        c.TipDiameter = vbit ? tip : t.TipDiameter;
        c.CutterMaterial = CutterMaterialBox.Text.Trim();
        c.Notes = NotesBox.Text;
        c.ShankDiameter = shank; c.OverallLength = overall; c.FluteLength = flute; c.StickoutLength = stickout;
        c.Vendor = VendorBox.Text.Trim(); c.ArticleNumber = ArticleBox.Text.Trim(); c.ProductLink = LinkBox.Text.Trim();
        c.FeedRate = f; c.PlungeRate = p; c.DepthPerPass = pass; c.SpindleRpm = rpm;
        c.CuttingData = cutting;
        if (c.Validate() is string invalid) { error = invalid; return null; }
        if (_tools.Any(o => o != t && o.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
        { error = "Det finns redan en fräs med det namnet."; return null; }
        if (number is int no && _tools.FirstOrDefault(o => o != t && o.ToolNumber == no) is { } other)
        { error = $"T{no} används redan av {other.Name}. Välj ett annat nummer (Nästa lediga) eller ta bort numret där först."; return null; }
        return c;
    }

    /// <summary>True when the fields differ from the saved tool (an invalid value counts as a change).</summary>
    private bool HasUnsavedChanges()
    {
        if (_shown is null) return false;
        var c = FromFields(out _);
        return c is null || JsonSerializer.Serialize(c, CompareJson) != JsonSerializer.Serialize(_shown, CompareJson);
    }

    /// <summary>Before leaving the shown tool: true = go on (saved or dropped), false = stay.</summary>
    private bool ConfirmLeave()
    {
        if (!HasUnsavedChanges()) return true;
        var answer = AppDialog.Choose(this, $"Du har ändrat i \"{_shown!.Name}\" utan att spara.", "Osparade ändringar",
            new[] { ("Spara fräsen", MessageBoxResult.Yes), ("Släng ändringarna", MessageBoxResult.No), ("Avbryt", MessageBoxResult.Cancel) },
            MessageBoxImage.Question, MessageBoxResult.Yes);
        return answer switch
        {
            MessageBoxResult.Yes => SaveShown(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    /// <summary>Writes the fields to the shown tool and saves the library. False (with the reason shown) if not saved.</summary>
    private bool SaveShown()
    {
        var t = _shown;
        if (t is null) return true;
        var c = FromFields(out var error);
        if (c is null) { Say(error ?? "Ogiltigt värde.", bad: true); return false; }
        int i = _tools.IndexOf(t);
        _tools[i] = c;
        if (!SaveLibrary($"{c.Name} sparad.")) { _tools[i] = t; return false; }
        RefreshCategories();
        Select(c);
        if (c.Warning is string w) Say(w, bad: true);
        return true;
    }

    /// <summary>Saves the whole library now. False (with the reason shown) if it could not be saved.</summary>
    private bool SaveLibrary(string done)
    {
        if (_save(Library()) is string error) { Say("Kunde inte spara verktygsbiblioteket: " + error, bad: true); return false; }
        SavedText.Text = $"{done}  Biblioteket sparat {DateTime.Now:HH:mm:ss} ({_tools.Count} fräsar).";
        Say("", bad: false);
        return true;
    }

    private static readonly System.Windows.Media.Brush Warn = Frozen(0xFF, 0xB3, 0x8A), Good = Frozen(0x7C, 0xD9, 0x9C);
    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var s = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)); s.Freeze(); return s;
    }

    private void Say(string text, bool bad) { MessageText.Foreground = bad ? Warn : Good; MessageText.Text = text; }

    private void SaveTool_Click(object sender, RoutedEventArgs e) => SaveShown();

    private void Revert_Click(object sender, RoutedEventArgs e) { Show(_shown); Say("Fräsen visas som den är sparad.", bad: false); }

    private void NextNumber_Click(object sender, RoutedEventArgs e) =>
        NumberBox.Text = new ToolLibrary { Tools = _tools.Where(t => t != _shown).ToList() }.NextFreeToolNumber().ToString(CultureInfo.InvariantCulture);

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        var link = LinkBox.Text.Trim();
        if (!Uri.TryCreate(link.Contains("://") ? link : "https://" + link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        { Say("Skriv en webbadress, t.ex. https://…", bad: true); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Say("Kunde inte öppna länken: " + ex.Message, bad: true); }
    }

    private void FillDefaults_Click(object sender, RoutedEventArgs e)
    {
        CuttingGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (CuttingGrid.SelectedItem is not CuttingRow r) { Say("Markera ett material i tabellen först.", bad: true); return; }
        r.Feed = FeedBox.Text; r.Plunge = PlungeBox.Text; r.Pass = PassBox.Text; r.Rpm = RpmBox.Text;
        CuttingGrid.Items.Refresh();
    }

    private void ClearRow_Click(object sender, RoutedEventArgs e)
    {
        CuttingGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (CuttingGrid.SelectedItem is not CuttingRow r) return;
        r.Feed = r.Plunge = r.Pass = r.Rpm = "";
        CuttingGrid.Items.Refresh();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeave()) return;
        var t = new ToolDefinition { Name = UniqueName("Ny fräs"), Category = _shown?.CategoryOrDefault ?? "" };
        AddTool(t, $"{t.Name} tillagd – fyll i den och klicka Spara fräsen.");
        NameBox.Focus(); NameBox.SelectAll();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is null || !ConfirmLeave()) return;
        var t = _shown.Clone();
        t.Id = Guid.NewGuid();
        t.Name = UniqueName(t.Name + " (kopia)");
        t.ToolNumber = null;           // a number belongs to one tool
        AddTool(t, $"{t.Name} tillagd.");
    }

    private void AddTool(ToolDefinition t, string done)
    {
        _tools.Add(t);
        if (!SaveLibrary(done)) { _tools.Remove(t); return; }
        RefreshCategories();
        Select(t);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var t = _shown;
        if (t is null) return;
        if (AppDialog.Show(this, $"Ta bort \"{t.Name}\" ur biblioteket?\n\nProjekt som använder fräsen behåller sina värden.",
                "Ta bort fräs", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int i = _tools.IndexOf(t);
        _tools.RemoveAt(i);
        if (!SaveLibrary($"{t.Name} borttagen.")) { _tools.Insert(i, t); return; }
        Select(_view.Cast<ToolDefinition>().FirstOrDefault());
    }

    // ---- Categories ----

    private void NewCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = AppDialog.Prompt(this, "Namn på den nya kategorin, t.ex. \"Kompressionsfräsar\".\n\nDen syns i listan när en fräs ligger i den – välj den under Kategori på en fräs.",
            "Ny kategori", okText: "Lägg till");
        if (name is null) return;
        var lib = Library();
        if (lib.AddCategory(name) is string error) { Say(error, bad: true); return; }
        ApplyCategories(lib, $"Kategorin {name} tillagd.");
    }

    private void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeave()) return;
        var lib = Library();
        var old = AppDialog.Prompt(this, "Vilken kategori vill du byta namn på?", "Byt namn på kategori",
            _shown?.CategoryOrDefault ?? "", lib.AllCategories(), choicesOnly: true, okText: "Nästa");
        if (string.IsNullOrEmpty(old)) return;
        var name = AppDialog.Prompt(this, $"Nytt namn på \"{old}\". Alla fräsar i kategorin följer med.", "Byt namn på kategori", old, okText: "Byt namn");
        if (name is null || name == old) return;
        if (lib.RenameCategory(old, name) is string error) { Say(error, bad: true); return; }
        ApplyCategories(lib, $"Kategorin {old} heter nu {name}.");
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeave()) return;
        var lib = Library();
        var all = lib.AllCategories();
        var name = AppDialog.Prompt(this, "Vilken kategori vill du ta bort?", "Ta bort kategori",
            _shown?.CategoryOrDefault ?? "", all, choicesOnly: true, okText: "Nästa");
        if (string.IsNullOrEmpty(name)) return;
        int count = _tools.Count(t => string.Equals(t.CategoryOrDefault, name, StringComparison.CurrentCultureIgnoreCase));
        string? moveTo = null;
        if (count > 0)
        {
            var others = all.Where(c => !string.Equals(c, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
            moveTo = AppDialog.Prompt(this, $"\"{name}\" har {count} fräs(ar). Vilken kategori ska de flyttas till? Välj eller skriv en ny.",
                "Ta bort kategori", others.FirstOrDefault() ?? "Övriga", others, okText: "Flytta och ta bort");
            if (string.IsNullOrEmpty(moveTo)) return;
        }
        else if (AppDialog.Show(this, $"Ta bort den tomma kategorin \"{name}\"?", "Ta bort kategori",
                     MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (lib.DeleteCategory(name, moveTo) is string error) { Say(error, bad: true); return; }
        ApplyCategories(lib, moveTo is null ? $"Kategorin {name} borttagen." : $"Kategorin {name} borttagen – fräsarna ligger nu i {moveTo}.");
    }

    /// <summary>Takes the categories (and the tools' new categories) from <paramref name="lib"/> and saves.</summary>
    private void ApplyCategories(ToolLibrary lib, string done)
    {
        // The tools in lib are the same objects as in the list, so their categories have already changed.
        var before = _categories;
        _categories = lib.Categories;
        if (!SaveLibrary(done)) { _categories = before; return; }
        RefreshCategories();
        Select(_shown);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmLeave()) e.Cancel = true;
    }

    private string UniqueName(string name)
    {
        var candidate = name; int i = 2;
        while (_tools.Any(t => t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) candidate = $"{name} {i++}";
        return candidate;
    }

    private static bool Optional(TextBox box, string what, out double? value, out string? error)
    {
        value = null; error = null;
        if (string.IsNullOrWhiteSpace(box.Text)) return true;
        if (!Num(box, out var v)) { error = $"Ange {what} som ett tal i mm, eller lämna fältet tomt."; return false; }
        value = v;
        return true;
    }

    private static bool Num(TextBox box, out double value) => Num(box.Text, out value);

    private static bool Num(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string F(double? v) => v is double d ? F(d) : "";
}
