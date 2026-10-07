using Avalonia.Controls;
using Avalonia.Layout;
using Composa.Editing;

namespace Composa.App;

public sealed partial class MainWindow
{
    private AlignmentReference alignmentReference = AlignmentReference.Canvas;

    private void BuildAlignmentFields(StackPanel row)
    {
        var s = session!;
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Margin = new Avalonia.Thickness(10, 0, 0, 0) };
        var reference = Ui.Combo(Enum.GetValues<AlignmentReference>(), alignmentReference,
            v => v == AlignmentReference.Canvas ? "Canvas" : "2nd object", v => { alignmentReference = v; refreshOptions?.Invoke(); }, 100);
        reference.Name = "AlignmentReference";
        ToolTip.SetTip(reference, "Align to the canvas or to the second object selected; that object stays in place. Shift-click adds objects.");
        group.Children.Add(reference);
        foreach (var (alignment, path) in new[]
        {
            (ObjectAlignment.Left, "M5 3 V21 M8 7 H19 V11 H8 Z M8 14 H15 V18 H8 Z"),
            (ObjectAlignment.Center, "M12 3 V21 M5 7 H19 V11 H5 Z M8 14 H16 V18 H8 Z"),
            (ObjectAlignment.Right, "M19 3 V21 M5 7 H16 V11 H5 Z M9 14 H16 V18 H9 Z"),
            (ObjectAlignment.Top, "M3 5 H21 M7 8 H11 V19 H7 Z M14 8 H18 V15 H14 Z"),
            (ObjectAlignment.Middle, "M3 12 H21 M7 5 H11 V19 H7 Z M14 8 H18 V16 H14 Z"),
            (ObjectAlignment.Bottom, "M3 19 H21 M7 5 H11 V16 H7 Z M14 9 H18 V16 H14 Z")
        })
        {
            var button = new Button { Classes = { "tool" }, Width = 26, Content = Icons.Create(new Icons.Icon(path), 16), Name = "Align" + alignment };
            ToolTip.SetTip(button, "Align " + alignment.ToString().ToLowerInvariant());
            button.Click += (_, _) => { s.AlignObjects(alignment, alignmentReference); refreshOptions?.Invoke(); };
            refreshOptions += () => button.IsEnabled = s.AlignableObjectCount >= (alignmentReference == AlignmentReference.Canvas ? 1 : 2);
            group.Children.Add(button);
        }
        foreach (var horizontal in new[] { true, false })
        {
            var button = new Button { Classes = { "tool" }, Width = 26, Content = Icons.Create(new Icons.Icon(horizontal
                ? "M3 4 V20 M21 4 V20 M8 7 H10 V17 H8 Z M14 7 H16 V17 H14 Z M4 12 H7 M11 12 H13 M17 12 H20"
                : "M4 3 H20 M4 21 H20 M7 8 H17 V10 H7 Z M7 14 H17 V16 H7 Z M12 4 V7 M12 11 V13 M12 17 V20"), 16), Name = horizontal ? "DistributeHorizontal" : "DistributeVertical" };
            ToolTip.SetTip(button, horizontal ? "Equal horizontal gaps (3+ objects)" : "Equal vertical gaps (3+ objects)");
            button.Click += (_, _) => { s.DistributeObjectGaps(horizontal); refreshOptions?.Invoke(); };
            refreshOptions += () => button.IsEnabled = s.AlignableObjectCount >= 3;
            group.Children.Add(button);
        }
        row.Children.Add(group);
        var flips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Avalonia.Thickness(10, 0, 0, 0) };
        foreach (var horizontal in new[] { true, false })
        {
            var button = new Button { Classes = { "tool" }, Width = 28, Name = horizontal ? "FlipHorizontal" : "FlipVertical",
                Content = Icons.Create(new Icons.Icon(horizontal ? "M12 3 V21 M3 7 L9 12 L3 17 Z M21 7 L15 12 L21 17 Z" : "M3 12 H21 M7 3 L12 9 L17 3 Z M7 21 L12 15 L17 21 Z"), 16) };
            ToolTip.SetTip(button, horizontal ? "Flip Layer Horizontal" : "Flip Layer Vertical");
            button.Click += (_, _) => { s.FlipLayers(horizontal); refreshOptions?.Invoke(); };
            refreshOptions += () => button.IsEnabled = s.ActiveLayer != null && !s.IsInteracting;
            flips.Children.Add(button);
        }
        row.Children.Add(flips);
        refreshOptions?.Invoke();
    }
}
