using System.Windows;
using System.Windows.Controls;
using CNCStudio.Core;

namespace CNCStudio.Desktop;

/// <summary>Objekt → Rotera 90°, and the right-click menu on a shape (rotate, ungroup, duplicate, remove).</summary>
public partial class MainWindow
{
    private void Rotate_Click(object sender, RoutedEventArgs e) =>
        RotateSelection(clockwise: (sender as FrameworkElement)?.Tag as string != "CCW");

    private void RotateSelection(bool clockwise)
    {
        var shapes = SelectedShapes();
        if (ShapeRotation.Problem(shapes) is string problem) { Error(problem); return; }
        RememberChange();
        bool hadText = shapes.Any(s => s.Text is not null);
        ShapeRotation.Rotate90(_document, shapes, clockwise);
        Render();
        bool grouped = shapes.Count > 1 && shapes.Select(s => s.GroupId).Distinct().Count() == 1 && shapes[0].GroupId is not null;
        StatusText.Text = $"{(shapes.Count == 1 ? "Objektet" : $"{shapes.Count} objekt")} vridet 90° {(clockwise ? "medurs" : "moturs")}." +
            (grouped ? " Hela gruppen vreds – avgruppera (Ctrl+Shift+G) för att vrida en del för sig." : "") +
            (hadText ? " Texten är nu konturer och kan inte längre ändras som text." : "");
    }

    /// <summary>Right-click on a shape: it is selected (with its group) and a menu for it opens.</summary>
    private bool ShapeContextMenu(object? source)
    {
        if (source is not DesignShape shape || _tool != Tool.Select) return false;
        if (!_selection.Contains(shape)) { SelectShape(shape, false); Render(); }
        var menu = new ContextMenu { PlacementTarget = DesignCanvas };
        MenuItem Item(string header, string? gesture, Action click)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
            return item;
        }
        Item("Rotera 90° medurs", "Ctrl+R", () => RotateSelection(true));
        Item("Rotera 90° moturs", "Ctrl+Shift+R", () => RotateSelection(false));
        menu.Items.Add(new Separator());
        if (_selection.Any(s => s.GroupId is not null)) Item("Avgruppera", "Ctrl+Shift+G", () => Ungroup_Click(this, new RoutedEventArgs()));
        if (_selection.Count > 1) Item("Gruppera", "Ctrl+G", () => Group_Click(this, new RoutedEventArgs()));
        Item("Duplicera", "Ctrl+D", () => Duplicate_Click(this, new RoutedEventArgs()));
        Item("Ta bort", "Delete", () => Delete_Click(this, new RoutedEventArgs()));
        menu.IsOpen = true;
        return true;
    }
}
