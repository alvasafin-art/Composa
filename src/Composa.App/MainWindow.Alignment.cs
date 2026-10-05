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
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var reference = Ui.Combo(Enum.GetValues<AlignmentReference>(), alignmentReference,
            v => v == AlignmentReference.Canvas ? "Canvas" : "First object", v => alignmentReference = v, 110);
        reference.Name = "AlignmentReference";
        ToolTip.SetTip(reference, "Align to the canvas or to the first object selected; Shift-click adds objects");
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
            var button = new Button { Classes = { "tool" }, Width = 28, Content = Icons.Create(new Icons.Icon(path), 16), Name = "Align" + alignment };
            ToolTip.SetTip(button, "Align " + alignment.ToString().ToLowerInvariant());
            button.Click += (_, _) => { s.AlignObjects(alignment, alignmentReference); refreshOptions?.Invoke(); };
            group.Children.Add(button);
        }
        foreach (var horizontal in new[] { true, false })
        {
            var button = new Button { Classes = { "tool" }, Width = 28, Content = Icons.Create(new Icons.Icon(horizontal
                ? "M3 4 V20 M21 4 V20 M8 7 H10 V17 H8 Z M14 7 H16 V17 H14 Z M4 12 H7 M11 12 H13 M17 12 H20"
                : "M4 3 H20 M4 21 H20 M7 8 H17 V10 H7 Z M7 14 H17 V16 H7 Z M12 4 V7 M12 11 V13 M12 17 V20"), 16), Name = horizontal ? "DistributeHorizontal" : "DistributeVertical" };
            ToolTip.SetTip(button, horizontal ? "Equal horizontal gaps (3+ objects)" : "Equal vertical gaps (3+ objects)");
            button.Click += (_, _) => { s.DistributeObjectGaps(horizontal); refreshOptions?.Invoke(); };
            group.Children.Add(button);
        }
        row.Children.Add(group);
    }
}
