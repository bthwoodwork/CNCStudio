using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Edits a copy of the tool library: tools in categories, default cutting data and cutting data per
/// material from the material library. <see cref="Result"/> is set only on Spara.
/// </summary>
public partial class ToolLibraryWindow : Window
{
    private readonly ObservableCollection<ToolDefinition> _tools;
    private readonly MaterialLibrary _materials;
    private readonly ObservableCollection<CuttingRow> _rows = new();
    private bool _loading;

    public ToolLibrary? Result { get; private set; }

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

    public ToolLibraryWindow(ToolLibrary library, MaterialLibrary materials, string? path = null)
    {
        InitializeComponent();
        _materials = materials;
        _tools = new ObservableCollection<ToolDefinition>(library.Tools.Select(t => t.Clone()));
        PathText.Text = path is null ? "" : "Sparas i " + path;
        var view = CollectionViewSource.GetDefaultView(_tools);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ToolDefinition.CategoryOrDefault)));
        view.SortDescriptions.Add(new SortDescription(nameof(ToolDefinition.CategoryOrDefault), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(ToolDefinition.Diameter), ListSortDirection.Ascending));
        ToolList.ItemsSource = view;
        CuttingGrid.ItemsSource = _rows;
        RefreshCategories();
        Select(_tools.OrderBy(t => t.CategoryOrDefault).ThenBy(t => t.Diameter).FirstOrDefault());
    }

    private ToolDefinition? Current => ToolList.SelectedItem as ToolDefinition;

    private void Select(ToolDefinition? tool)
    {
        CollectionViewSource.GetDefaultView(_tools).Refresh();
        ToolList.SelectedItem = tool;
        Editor.IsEnabled = tool is not null;
    }

    private void RefreshCategories()
    {
        var text = CategoryBox.Text;
        CategoryBox.ItemsSource = _tools.Select(t => t.CategoryOrDefault)
            .Concat(new[] { "Raka fräsar", "Kulfräsar", "V-fräsar", "Kompressionsfräsar", "Planfräsar" })
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
        CategoryBox.Text = text;
    }

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var t = Current;
        Editor.IsEnabled = t is not null;
        if (t is null) return;
        _loading = true;
        NameBox.Text = t.Name;
        CategoryBox.Text = t.CategoryOrDefault;
        KindBox.SelectedIndex = (int)t.Kind;
        DiameterBox.Text = F(t.Diameter); AngleBox.Text = F(t.AngleDeg);
        FlutesBox.Text = t.Flutes.ToString(CultureInfo.InvariantCulture);
        FeedBox.Text = F(t.FeedRate); PlungeBox.Text = F(t.PlungeRate);
        PassBox.Text = F(t.DepthPerPass); RpmBox.Text = F(t.SpindleRpm); NotesBox.Text = t.Notes;
        _rows.Clear();
        foreach (var m in _materials.Materials.OrderBy(m => m.Category).ThenBy(m => m.Name))
            _rows.Add(Row(m.Id, m.DisplayName, m.Name, t.CuttingData.FirstOrDefault(c => c.MaterialId == m.Id)));
        // Cutting data for materials that are no longer in the library is kept and shown.
        foreach (var c in t.CuttingData.Where(c => _materials.Find(c.MaterialId) is null))
            _rows.Add(Row(c.MaterialId, c.MaterialName + " (finns inte i materialbiblioteket)", c.MaterialName, c));
        MessageText.Text = _materials.Materials.Count == 0 ? "Inga material ännu – lägg upp dem i Materialbiblioteket." : "";
        _loading = false;
        UpdateAngleVisibility();
    }

    private static CuttingRow Row(Guid id, string label, string name, MaterialCuttingData? c) => new()
    {
        MaterialId = id, Material = label, MaterialName = name,
        Feed = c is null ? "" : F(c.FeedRate), Plunge = c is null ? "" : F(c.PlungeRate),
        Pass = c is null ? "" : F(c.DepthPerPass), Rpm = c is null ? "" : F(c.SpindleRpm)
    };

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdateAngleVisibility(); }

    private void UpdateAngleVisibility()
    {
        if (AngleBox is null) return;
        var v = KindBox.SelectedIndex == (int)ToolKind.VBit ? Visibility.Visible : Visibility.Collapsed;
        AngleBox.Visibility = v; AngleLabel.Visibility = v;
    }

    /// <summary>Copies the fields into the selected tool. Returns false (and shows why) if a value is invalid.</summary>
    private bool ApplyFields()
    {
        var t = Current;
        if (t is null) return true;
        CuttingGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!Num(DiameterBox, out var d) || !Num(FeedBox, out var f) || !Num(PlungeBox, out var p) ||
            !Num(PassBox, out var pass) || !Num(RpmBox, out var rpm) ||
            !int.TryParse(FlutesBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var flutes))
        { MessageText.Text = "Ange giltiga tal i alla fält."; return false; }
        double angle = t.AngleDeg;
        if (KindBox.SelectedIndex == (int)ToolKind.VBit && !Num(AngleBox, out angle))
        { MessageText.Text = "Ange V-fräsens vinkel."; return false; }

        var cutting = new List<MaterialCuttingData>();
        foreach (var r in _rows)
        {
            if (r.IsEmpty) continue;
            if (!Num(r.Feed, out var cf) || !Num(r.Plunge, out var cp) || !Num(r.Pass, out var cpass) || !Num(r.Rpm, out var crpm))
            { MessageText.Text = $"{r.MaterialName}: fyll i alla fyra värdena med tal, eller töm raden."; return false; }
            cutting.Add(new MaterialCuttingData { MaterialId = r.MaterialId, MaterialName = r.MaterialName, FeedRate = cf, PlungeRate = cp, DepthPerPass = cpass, SpindleRpm = crpm });
        }

        var candidate = t.Clone();
        candidate.Name = NameBox.Text.Trim(); candidate.Kind = (ToolKind)Math.Max(0, KindBox.SelectedIndex);
        candidate.Category = CategoryBox.Text.Trim();
        candidate.Diameter = d; candidate.AngleDeg = angle; candidate.Flutes = flutes;
        candidate.FeedRate = f; candidate.PlungeRate = p; candidate.DepthPerPass = pass; candidate.SpindleRpm = rpm;
        candidate.Notes = NotesBox.Text;
        candidate.CuttingData = cutting;
        if (candidate.Validate() is string error) { MessageText.Text = error; return false; }
        if (_tools.Any(o => o != t && o.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)))
        { MessageText.Text = "Det finns redan en fräs med det namnet."; return false; }
        _tools[_tools.IndexOf(t)] = candidate;
        RefreshCategories();
        Select(candidate);
        MessageText.Text = "";
        return true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) { if (ApplyFields()) MessageText.Text = "Fräsen uppdaterad – klicka Spara för att spara biblioteket."; }

    private void FillDefaults_Click(object sender, RoutedEventArgs e)
    {
        CuttingGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (CuttingGrid.SelectedItem is not CuttingRow r) { MessageText.Text = "Markera ett material i tabellen först."; return; }
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
        if (!ApplyFields()) return;
        var t = new ToolDefinition { Name = UniqueName("Ny fräs"), Category = Current?.CategoryOrDefault ?? "" };
        _tools.Add(t);
        Select(t);
        NameBox.Focus(); NameBox.SelectAll();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Current is null || !ApplyFields()) return;
        var t = Current!.Clone();
        t.Id = Guid.NewGuid();
        t.Name = UniqueName(t.Name + " (kopia)");
        _tools.Add(t);
        Select(t);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var t = Current;
        if (t is null) return;
        if (AppDialog.Show(this, $"Ta bort \"{t.Name}\" ur biblioteket?\n\nProjekt som använder fräsen behåller sina värden.",
                "Ta bort fräs", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _tools.Remove(t);
        Select(_tools.FirstOrDefault());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyFields()) return;
        Result = new ToolLibrary { Tools = _tools.ToList() };
        DialogResult = true;
    }

    private string UniqueName(string name)
    {
        var candidate = name; int i = 2;
        while (_tools.Any(t => t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) candidate = $"{name} {i++}";
        return candidate;
    }

    private static bool Num(TextBox box, out double value) => Num(box.Text, out value);

    private static bool Num(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
