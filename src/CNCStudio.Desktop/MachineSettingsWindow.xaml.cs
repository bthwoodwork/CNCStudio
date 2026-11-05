using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CNCStudio.Core.Grbl;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>
/// Machine profile: work area, Z travel, homing, limit switches and safety margin.
/// Edits a copy; <see cref="Result"/> is set only when the user saves valid values.
/// </summary>
public partial class MachineSettingsWindow : Window
{
    private readonly MachineProfile _original;
    private readonly Func<MachineProfile, string?> _validate;

    public MachineProfile? Result { get; private set; }

    private readonly GrblClient? _grbl;
    private IReadOnlyDictionary<int, double>? _machineSettings;
    private readonly List<SettingRow> _rows = new();

    /// <summary>One row in the $$ grid.</summary>
    public sealed class SettingRow
    {
        public int Number { get; init; }
        public string Code => "$" + Number.ToString(CultureInfo.InvariantCulture);
        public string Name { get; init; } = "";
        public string Unit { get; init; } = "";
        public double Machine { get; set; }
        public string MachineValue => Machine.ToString("0.###", CultureInfo.InvariantCulture);
        public string NewValue { get; set; } = "";
    }

    public MachineSettingsWindow(MachineProfile current, Func<MachineProfile, string?> validate, GrblClient? grbl = null)
    {
        InitializeComponent();
        _original = current;
        _validate = validate;
        _grbl = grbl;
        GrblInfoText.Text = grbl is null
            ? "Inte ansluten. Anslut i Maskinkontroll-panelen för att läsa och skriva maskinens inställningar."
            : "Läser hela inställningsminnet med $$. Ändrade värden skrivs ett i taget direkt till maskinens EEPROM " +
              "(bara när maskinen är Idle eller Alarm) och kontrolläses sedan. Detta är skilt från Spara/Avbryt nedan.";
        ReadGrblButton.IsEnabled = grbl is not null;
        NameBox.Text = current.Name;
        XBox.Text = F(current.WorkWidthMm);
        YBox.Text = F(current.WorkLengthMm);
        ZBox.Text = F(current.ZTravelMm);
        HomeOnStartupBox.IsChecked = current.HomeOnStartup;
        InvertXBox.IsChecked = (current.DirInvertMask & 1) != 0;
        InvertYBox.IsChecked = (current.DirInvertMask & 2) != 0;
        InvertZBox.IsChecked = (current.DirInvertMask & 4) != 0;
        HomeXNegBox.IsChecked = (current.HomingDirInvertMask & 1) != 0;
        HomeYNegBox.IsChecked = (current.HomingDirInvertMask & 2) != 0;
        UpdateMaskTexts();
        PullOffBox.Text = F(current.HomingPullOffMm);
        SoftLimitsBox.IsChecked = current.SoftLimits;
        SpindleManualBox.IsChecked = current.Spindle == SpindleControl.Manual;
        SpindleGrblBox.IsChecked = current.Spindle == SpindleControl.Grbl;
        MarginBox.Text = F(current.LimitMarginMm);
        HomeOnStartupBox.Checked += (_, _) => UpdateWarning();
        HomeOnStartupBox.Unchecked += (_, _) => UpdateWarning();
        SoftLimitsBox.Checked += (_, _) => UpdateWarning();
        SoftLimitsBox.Unchecked += (_, _) => UpdateWarning();
        UpdateWarning();
    }

    private void UpdateWarning()
    {
        var notes = new List<string>();
        if (HomeOnStartupBox.IsChecked != true)
            notes.Add("Utan homing vid start är maskinkoordinaterna och Z-brytarens läge okända – Z-kontrollerna gäller då inte.");
        if (SoftLimitsBox.IsChecked != true)
            notes.Add("Utan mjuka gränser stoppar GRBL först när en brytare nås.");
        WarningText.Text = string.Join("\n", notes);
    }

    private async void ReadGrbl_Click(object sender, RoutedEventArgs e)
    {
        if (_grbl is null) return;
        SetGrblBusy(true, "Läser $$ …");
        try
        {
            _machineSettings = await _grbl.ReadSettingsAsync();
            FillGrid(_machineSettings);
            ShowProfileComparison();
        }
        catch (Exception ex) { GrblResultText.Text = "Kunde inte läsa: " + ex.Message; }
        finally { SetGrblBusy(false, null); }
    }

    private void FillGrid(IReadOnlyDictionary<int, double> settings)
    {
        _rows.Clear();
        foreach (var (n, v) in settings)
        {
            GrblSettings.Catalog.TryGetValue(n, out var info);
            _rows.Add(new SettingRow { Number = n, Name = info?.Name ?? "(okänd i GRBL 1.1)", Unit = info?.Unit ?? "",
                Machine = v, NewValue = v.ToString("0.###", CultureInfo.InvariantCulture) });
        }
        GrblGrid.ItemsSource = null;
        GrblGrid.ItemsSource = _rows;
        ApplyGrblButton.IsEnabled = true;
        UpdateWriteButton();
    }

    private void ShowProfileComparison()
    {
        if (_machineSettings is null) return;
        var current = ReadForm() ?? _original;
        var diffs = GrblSettings.CompareWithProfile(current, _machineSettings);
        var unitsWarning = _machineSettings.TryGetValue(13, out var inch) && inch == 1
            ? "OBS: $13=1 – maskinen rapporterar i tum. CNC Studio räknar i mm.\n" : "";
        GrblResultText.Foreground = diffs.Count == 0 && unitsWarning.Length == 0 ? Brushes.LightGreen : Brushes.Orange;
        GrblResultText.Text = unitsWarning + (diffs.Count == 0
            ? $"Läste {_machineSettings.Count} inställningar. Maskinprofilen stämmer med maskinen."
            : $"Läste {_machineSettings.Count} inställningar. Skillnader mot maskinprofilen:\n" +
              string.Join("\n", diffs.Select(d => $"  ${d.Setting} {d.What}: profil {d.ProfileValue}, maskin {d.MachineValue}")));
    }

    private void GrblGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateWriteButton, System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>Rows whose new value differs from the machine; null if a value is not a number.</summary>
    private List<(SettingRow Row, double Value)>? Changes(out string? error)
    {
        error = null;
        var list = new List<(SettingRow, double)>();
        foreach (var r in _rows)
        {
            if (!double.TryParse(r.NewValue.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            { error = $"{r.Code}: \"{r.NewValue}\" är inget tal."; return null; }
            if (Math.Abs(v - r.Machine) > 0.0005) list.Add((r, v));
        }
        return list;
    }

    private void UpdateWriteButton()
    {
        var changes = Changes(out _);
        WriteGrblButton.IsEnabled = _grbl is not null && changes is { Count: > 0 };
        WriteGrblButton.Content = changes is { Count: > 0 } ? $"Skriv {changes.Count} ändrade till maskinen…" : "Skriv ändrade till maskinen…";
    }

    private async void WriteGrbl_Click(object sender, RoutedEventArgs e)
    {
        if (_grbl is null) return;
        var changes = Changes(out var error);
        if (changes is null) { Show(error!); return; }
        if (changes.Count == 0) return;
        foreach (var (row, value) in changes)
            if (GrblProtocol.FormatSettingWrite(row.Number, value, out _) is string problem) { Show(problem); return; }
        var text = "Följande skrivs direkt till maskinens inställningsminne (EEPROM), en i taget:\n\n" +
            string.Join("\n", changes.Select(c => $"  {c.Row.Code}  {c.Row.Name}: {c.Row.MachineValue} → " +
                $"{c.Value.ToString("0.###", CultureInfo.InvariantCulture)} {c.Row.Unit}")) +
            "\n\nMaskinen måste vara Idle eller Alarm. Efteråt läses $$ om och varje värde kontrolleras." +
            (changes.Any(c => c.Row.Number is >= 100 and <= 102) ? "\n\nOBS: steg/mm ($100–$102) ändrar hur långt axlarna faktiskt går. Mät efter kalibreringen." : "") +
            (changes.Any(c => c.Row.Number is 3 or 23) ? "\n\nOBS: riktning ($3) eller homingriktning ($23) ändras. Testa med korta jogg innan homing." : "") +
            "\n\nSkriva till maskinen?";
        if (MessageBox.Show(this, text, "Skriv till maskinen", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        SetGrblBusy(true, "Skriver …");
        try
        {
            var result = await _grbl.WriteSettingsAsync(changes.Select(c => (c.Row.Number, c.Value)).ToList());
            if (result.VerifiedSettings is not null) { _machineSettings = result.VerifiedSettings; FillGrid(_machineSettings); }
            GrblResultText.Foreground = result.Success ? Brushes.LightGreen : Brushes.OrangeRed;
            GrblResultText.Text = result.Message;
            if (!result.Success) Show(result.Message);
            else ShowProfileComparisonAppend(result.Message);
        }
        catch (Exception ex) { GrblResultText.Foreground = Brushes.OrangeRed; GrblResultText.Text = "Skrivningen avbröts: " + ex.Message; }
        finally { SetGrblBusy(false, null); }
    }

    private void ShowProfileComparisonAppend(string message)
    {
        ShowProfileComparison();
        GrblResultText.Text = message + "\n" + GrblResultText.Text;
    }

    /// <summary>Fills the profile form with the controller's values (still needs Spara to take effect).</summary>
    private void ApplyGrbl_Click(object sender, RoutedEventArgs e)
    {
        if (_machineSettings is null) return;
        var applied = GrblSettings.ApplyToProfile(ReadForm() ?? _original, _machineSettings);
        _applied = applied;
        XBox.Text = F(applied.WorkWidthMm); YBox.Text = F(applied.WorkLengthMm); ZBox.Text = F(applied.ZTravelMm);
        PullOffBox.Text = F(applied.HomingPullOffMm); MarginBox.Text = F(applied.LimitMarginMm);
        SoftLimitsBox.IsChecked = applied.SoftLimits; HomeOnStartupBox.IsChecked = applied.HomeOnStartup;
        InvertXBox.IsChecked = (applied.DirInvertMask & 1) != 0;
        InvertYBox.IsChecked = (applied.DirInvertMask & 2) != 0;
        InvertZBox.IsChecked = (applied.DirInvertMask & 4) != 0;
        HomeXNegBox.IsChecked = (applied.HomingDirInvertMask & 1) != 0;
        HomeYNegBox.IsChecked = (applied.HomingDirInvertMask & 2) != 0;
        _calibrated = true;
        UpdateMaskTexts(); UpdateWarning(); ShowProfileComparison();
        GrblResultText.Text = "Maskinens värden är ifyllda i profilen till vänster. Klicka Spara för att använda dem.\n" + GrblResultText.Text;
        if ((applied.HomingDirInvertMask & 4) != 0)
            Show("Maskinens $23 har Z-biten satt (Z homar nedåt). CNC Studio kräver att Z homar uppåt; profilen kan inte sparas så.");
    }

    private bool _calibrated;
    private MachineProfile? _applied;

    private void SetGrblBusy(bool busy, string? text)
    {
        ReadGrblButton.IsEnabled = !busy && _grbl is not null;
        WriteGrblButton.IsEnabled = !busy && _grbl is not null && Changes(out _) is { Count: > 0 };
        ApplyGrblButton.IsEnabled = !busy && _machineSettings is not null;
        if (text is not null) { GrblResultText.Foreground = Brushes.White; GrblResultText.Text = text; }
    }

    /// <summary>The profile as currently entered, or null if a number field is invalid.</summary>
    private MachineProfile? ReadForm()
    {
        if (!Num(XBox, out var x) || !Num(YBox, out var y) || !Num(ZBox, out var z) ||
            !Num(PullOffBox, out var pullOff) || !Num(MarginBox, out var margin)) return null;
        return new MachineProfile
        {
            Name = NameBox.Text.Trim(), WorkWidthMm = x, WorkLengthMm = y, ZTravelMm = z,
            HomeOnStartup = HomeOnStartupBox.IsChecked == true,
            DirInvertMask = DirMask(), HomingDirInvertMask = HomingMask(),
            HomingPullOffMm = pullOff, SoftLimits = SoftLimitsBox.IsChecked == true, LimitMarginMm = margin,
            CalibratedToGrbl = _calibrated || _original.CalibratedToGrbl,
            Spindle = SpindleGrblBox.IsChecked == true ? SpindleControl.Grbl : SpindleControl.Manual,
            MaxRateX = (_applied ?? _original).MaxRateX, MaxRateY = (_applied ?? _original).MaxRateY,
            MaxRateZ = (_applied ?? _original).MaxRateZ
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Num(XBox, out var x) || !Num(YBox, out var y) || !Num(ZBox, out var z) ||
            !Num(PullOffBox, out var pullOff) || !Num(MarginBox, out var margin))
        { Show("Ange giltiga tal i alla fält."); return; }
        var candidate = new MachineProfile
        {
            Name = NameBox.Text.Trim(),
            WorkWidthMm = x, WorkLengthMm = y, ZTravelMm = z,
            HomeOnStartup = HomeOnStartupBox.IsChecked == true,
            DirInvertMask = DirMask(),
            HomingDirInvertMask = HomingMask(),
            HomingPullOffMm = pullOff,
            SoftLimits = SoftLimitsBox.IsChecked == true,
            LimitMarginMm = margin,
            CalibratedToGrbl = _calibrated || _original.CalibratedToGrbl,
            Spindle = SpindleGrblBox.IsChecked == true ? SpindleControl.Grbl : SpindleControl.Manual,
            MaxRateX = (_applied ?? _original).MaxRateX, MaxRateY = (_applied ?? _original).MaxRateY,
            MaxRateZ = (_applied ?? _original).MaxRateZ
        };
        if (_validate(candidate) is string error) { Show(error); return; }
        Result = candidate;
        DialogResult = true;
    }

    /// <summary>$3 from the checkboxes: X = 1, Y = 2, Z = 4 (verified, GRBL 1.1 wiki).</summary>
    private int DirMask() =>
        (InvertXBox.IsChecked == true ? 1 : 0) | (InvertYBox.IsChecked == true ? 2 : 0) | (InvertZBox.IsChecked == true ? 4 : 0);

    /// <summary>$23 from the checkboxes. Z never homes negative here (switch at the top), so bit 4 stays clear.</summary>
    private int HomingMask() =>
        (HomeXNegBox.IsChecked == true ? 1 : 0) | (HomeYNegBox.IsChecked == true ? 2 : 0);

    private void Mask_Changed(object sender, RoutedEventArgs e) => UpdateMaskTexts();

    /// <summary>Shows the exact GRBL values to compare with $$ and what they mean.</summary>
    private void UpdateMaskTexts()
    {
        if (DirInvertText is null || DirMaskText is null || InvertXBox is null || HomeXNegBox is null) return;
        int dir = DirMask(), home = HomingMask();
        var inverted = new[] { (1, "X"), (2, "Y"), (4, "Z") }.Where(a => (dir & a.Item1) != 0).Select(a => a.Item2).ToList();
        DirInvertText.Text = $"Ska stå $3={dir} i GRBL. " + (inverted.Count == 0
            ? "Ingen axel inverteras."
            : $"Inverterar riktningssignalen för {string.Join(", ", inverted)}. Påverkar inte koordinaterna – bara åt vilket håll motorn går.");
        var corner = new MachineProfile { HomingDirInvertMask = home }.HomeCorner switch
        {
            HomeCorner.BackLeft => "bak vänster", HomeCorner.BackRight => "bak höger",
            HomeCorner.FrontLeft => "fram vänster", _ => "fram höger"
        };
        DirMaskText.Text = $"Ska stå $23={home} i GRBL. X homar {((home & 1) != 0 ? "− (vänster)" : "+ (höger)")}, " +
            $"Y homar {((home & 2) != 0 ? "− (fram)" : "+ (bak)")}, Z homar + (upp). Hem: {corner}. " +
            "Ställ in $3 först så att plusriktningarna stämmer.";
    }

    private void Show(string message) =>
        MessageBox.Show(this, message, "Maskininställningar", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static bool Num(TextBox box, out double value) =>
        double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
