using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Edits a copy of the tool library; <see cref="Result"/> is set only on Spara.</summary>
public partial class ToolLibraryWindow : Window
{
    private readonly List<ToolDefinition> _tools;
    private bool _loading;

    public ToolLibrary? Result { get; private set; }

    public ToolLibraryWindow(ToolLibrary library)
    {
        InitializeComponent();
        _tools = library.Tools.Select(t => t.Clone()).ToList();
        RefreshList(_tools.FirstOrDefault());
    }

    private ToolDefinition? Current => ToolList.SelectedItem as ToolDefinition;

    private void RefreshList(ToolDefinition? select)
    {
        ToolList.ItemsSource = null;
        ToolList.ItemsSource = _tools;
        ToolList.SelectedItem = select;
        Editor.IsEnabled = select is not null;
    }

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var t = Current;
        Editor.IsEnabled = t is not null;
        if (t is null) return;
        _loading = true;
        NameBox.Text = t.Name;
        KindBox.SelectedIndex = (int)t.Kind;
        DiameterBox.Text = F(t.Diameter); AngleBox.Text = F(t.AngleDeg);
        FlutesBox.Text = t.Flutes.ToString(CultureInfo.InvariantCulture);
        FeedBox.Text = F(t.FeedRate); PlungeBox.Text = F(t.PlungeRate);
        PassBox.Text = F(t.DepthPerPass); RpmBox.Text = F(t.SpindleRpm); NotesBox.Text = t.Notes;
        MessageText.Text = "";
        _loading = false;
        UpdateAngleVisibility();
    }

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
        if (!Num(DiameterBox, out var d) || !Num(FeedBox, out var f) || !Num(PlungeBox, out var p) ||
            !Num(PassBox, out var pass) || !Num(RpmBox, out var rpm) ||
            !int.TryParse(FlutesBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var flutes))
        { MessageText.Text = "Ange giltiga tal i alla fält."; return false; }
        double angle = t.AngleDeg;
        if (KindBox.SelectedIndex == (int)ToolKind.VBit && !Num(AngleBox, out angle))
        { MessageText.Text = "Ange V-fräsens vinkel."; return false; }
        var candidate = t.Clone();
        candidate.Name = NameBox.Text.Trim(); candidate.Kind = (ToolKind)Math.Max(0, KindBox.SelectedIndex);
        candidate.Diameter = d; candidate.AngleDeg = angle; candidate.Flutes = flutes;
        candidate.FeedRate = f; candidate.PlungeRate = p; candidate.DepthPerPass = pass; candidate.SpindleRpm = rpm;
        candidate.Notes = NotesBox.Text;
        if (candidate.Validate() is string error) { MessageText.Text = error; return false; }
        if (_tools.Any(o => o != t && o.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)))
        { MessageText.Text = "Det finns redan en fräs med det namnet."; return false; }
        var index = _tools.IndexOf(t);
        _tools[index] = candidate;
        RefreshList(candidate);
        MessageText.Text = "";
        return true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyFields();

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyFields()) return;
        var t = new ToolDefinition { Name = UniqueName("Ny fräs") };
        _tools.Add(t);
        RefreshList(t);
        NameBox.Focus(); NameBox.SelectAll();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Current is null || !ApplyFields()) return;
        var t = Current!.Clone();
        t.Id = Guid.NewGuid();
        t.Name = UniqueName(t.Name + " (kopia)");
        _tools.Add(t);
        RefreshList(t);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var t = Current;
        if (t is null) return;
        if (AppDialog.Show(this, $"Ta bort \"{t.Name}\" ur biblioteket?\n\nProjekt som använder fräsen behåller sina värden.",
                "Ta bort fräs", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _tools.Remove(t);
        RefreshList(_tools.FirstOrDefault());
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

    private static bool Num(TextBox box, out double value) =>
        double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
