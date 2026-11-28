using Avalonia.Controls;
using Avalonia.Layout;
using Composa.Editing;

namespace Composa.App;

public sealed partial class MainWindow
{
    private AlignmentReference alignmentReference = AlignmentReference.Selection;

    private void BuildAlignmentFields(StackPanel row)
    {
        var s = session!;
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Margin = new Avalonia.Thickness(10, 0, 0, 0) };
        var reference = Ui.Combo(new[] { AlignmentReference.Selection, AlignmentReference.Canvas }, alignmentReference,
            v => v == AlignmentReference.Canvas ? "Align to Canvas" : "Selection", v => { alignmentReference = v; refreshOptions?.Invoke(); }, 127);
        reference.Name = "AlignmentReference";
        Locale.Tip(reference, "Align to the selection bounds. Click a selected object again to keep it in place as the key object.");
        row.Children.Add(reference);
        var alignButtons = new List<Button>();
        var distributeButtons = new List<Button>();
        foreach (var (alignment, glyph) in new[]
        {
            (ObjectAlignment.Left, "align-object-left"), (ObjectAlignment.Center, "align-object-center"),
            (ObjectAlignment.Right, "align-object-right"), (ObjectAlignment.Top, "align-object-top"),
            (ObjectAlignment.Middle, "align-object-middle"), (ObjectAlignment.Bottom, "align-object-bottom")
        })
        {
            var button = new Button { Classes = { "flat", "alignment" }, Padding = new Avalonia.Thickness(4, 0), HorizontalContentAlignment = HorizontalAlignment.Center, Width = 26, Content = Icons.Create(Icons.Packed(glyph), 16), Name = "Align" + alignment };
            Locale.Tip(button, Locale.T("Align " + alignment.ToString().ToLowerInvariant()));
            button.Click += (_, _) => { s.AlignObjects(alignment, alignmentReference); refreshOptions?.Invoke(); };
            alignButtons.Add(button);
            group.Children.Add(button);
        }
        foreach (var horizontal in new[] { true, false })
        {
            var button = new Button { Classes = { "flat", "alignment" }, Padding = new Avalonia.Thickness(4, 0), HorizontalContentAlignment = HorizontalAlignment.Center, Width = 26, Content = Icons.Create(Icons.Packed(horizontal ? "distribute-x" : "distribute-y"), 16), Name = horizontal ? "DistributeHorizontal" : "DistributeVertical" };
            Locale.Tip(button, Locale.T(horizontal ? "Equal horizontal gaps (3+ objects)" : "Equal vertical gaps (3+ objects)"));
            button.Click += (_, _) => { s.DistributeObjectGaps(horizontal); refreshOptions?.Invoke(); };
            distributeButtons.Add(button);
            group.Children.Add(button);
        }
        refreshOptions += () =>
        {
            var count = s.AlignableObjectCount;
            foreach (var button in alignButtons) button.IsEnabled = count >= 1;
            foreach (var button in distributeButtons) button.IsEnabled = count >= 3;
        };
        row.Children.Add(group);
        var flips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Avalonia.Thickness(10, 0, 0, 0) };
        foreach (var horizontal in new[] { true, false })
        {
            var button = new Button { Classes = { "flat", "alignment" }, Padding = new Avalonia.Thickness(4, 0), HorizontalContentAlignment = HorizontalAlignment.Center, Width = 28, Name = horizontal ? "FlipHorizontal" : "FlipVertical",
                Content = Icons.Create(Icons.Packed(horizontal ? "flip-x" : "flip-y"), 16) };
            Locale.Tip(button, Locale.T(horizontal ? "Flip Layer Horizontal" : "Flip Layer Vertical"));
            button.Click += (_, _) => { s.FlipLayers(horizontal); refreshOptions?.Invoke(); };
            refreshOptions += () => button.IsEnabled = s.ActiveLayer != null && !s.IsInteracting;
            flips.Children.Add(button);
        }
        row.Children.Add(flips);
        var turns = Ui.Row(2);
        turns.Margin = new Avalonia.Thickness(8, 0, 0, 0);
        foreach (var (angle, name, tip, glyph) in new[]
        {
            (-90, "RotateLeft", "Rotate Layer 90° Counterclockwise", "rotate-left"),
            (90, "RotateRight", "Rotate Layer 90° Clockwise", "rotate-right")
        })
        {
            var button = Ui.IconButton(Icons.Packed(glyph), tip, () => s.RotateLayers(angle));
            button.Classes.Add("flat");
            button.Classes.Add("alignment");
            button.Name = name;
            button.Width = 28;
            button.MinWidth = 0;
            button.Padding = default;
            refreshOptions += () => button.IsEnabled = s.ActiveLayer != null && !s.IsInteracting;
            turns.Children.Add(button);
        }
        row.Children.Add(turns);
        refreshOptions?.Invoke();
    }
}
