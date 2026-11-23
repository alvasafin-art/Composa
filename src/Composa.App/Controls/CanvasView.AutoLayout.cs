using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    private AutoLayoutProposal? layoutProposal;
    private (Guid Group, Guid Child, double Gap, bool Row, float GrabOffset)? layoutGesture;
    private int layoutGapIndex = -1;
    private int layoutDropIndex = -1;
    private (OverlayLayer Overlay, Panel Host)? layoutGapEditor;

    internal void SetLayoutProposal(AutoLayoutProposal? proposal)
    {
        layoutProposal = proposal;
        InvalidateCanvas();
    }

    private Layer? SelectedLayoutItem()
    {
        if (session is not { Tool: Tool.Move, IsEditingMask: false, IsEditingChannel: false } target
            || target.Document.SelectedLayerIds.Count != 1)
            return null;
        return target.ActiveLayer;
    }

    private Layer? SelectedLayoutGroup() => SelectedLayoutItem() is { AutoLayout: not null } group ? group : null;

    private (Rect Band, Rect Label) GapGeometry(Layer group, IReadOnlyList<LayoutElement> elements, int index)
    {
        var first = elements[index].Bounds;
        var next = elements[index + 1].Bounds;
        var frame = EditorSession.LayoutBounds(group);
        var row = group.AutoLayout!.Direction == LayoutDirection.Row;
        var band = row
            ? ScreenRect(new(first.Right, frame.Top, next.Left, frame.Bottom))
            : ScreenRect(new(frame.Left, first.Bottom, frame.Right, next.Top));
        var width = Math.Max(42, $"{group.AutoLayout.Gap:0.##} px".Length * 7 + 8);
        var label = row
            ? new Rect(band.Center.X - width / 2, Math.Max(0, band.Top - 23), width, 20)
            : new Rect(Math.Clamp(band.Right + 6, 0, Math.Max(0, Bounds.Width - width)), band.Center.Y - 10, width, 20);
        return (band, label);
    }

    private Rect ScreenRect(SKRect area) => new(ToScreen(new(area.Left, area.Top)), ToScreen(new(area.Right, area.Bottom)));

    private int LayoutGapAt(Layer group, IReadOnlyList<LayoutElement> elements, Point screen)
    {
        for (var i = 0; i + 1 < elements.Count; i++)
        {
            var (band, label) = GapGeometry(group, elements, i);
            var mayUseLabel = layoutGapIndex == i && band.Union(label).Contains(screen);
            if (mayUseLabel || band.Inflate(group.AutoLayout!.Direction == LayoutDirection.Row ? new Thickness(3, 0) : new Thickness(0, 3)).Contains(screen))
                return i;
        }
        return -1;
    }

    private void UpdateLayoutHover(Point position)
    {
        if (SelectedLayoutGroup() is not { } group || session == null || layoutProposal != null)
        {
            layoutGapIndex = -1;
            return;
        }
        var elements = session.LayoutElements(group);
        layoutGapIndex = LayoutGapAt(group, elements, position);
        if (layoutGapIndex >= 0 && GapGeometry(group, elements, layoutGapIndex).Label.Contains(position))
            Cursor = new Cursor(group.AutoLayout!.Direction == LayoutDirection.Row ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
    }

    private bool BeginLayoutGesture(Point position, bool allowReorder)
    {
        if (SelectedLayoutItem() is not { } selected || session == null || layoutProposal != null || session.HasPendingEdit) return false;
        if (selected.AutoLayout is { } layout && session.CanEditAutoLayout(selected) && layoutGapIndex >= 0)
        {
            var units = session.LayoutElements(selected);
            if (layoutGapIndex < units.Count - 1 && GapGeometry(selected, units, layoutGapIndex).Label.Contains(position))
            {
                layoutGesture = (selected.Id, Guid.Empty, layout.Gap, layout.Direction == LayoutDirection.Row, 0);
                drag = Drag.LayoutGap;
                return true;
            }
        }
        return allowReorder && BeginLayoutReorder();
    }

    private bool BeginLayoutReorder()
    {
        if (SelectedLayoutItem() is not { } selected || session == null || layoutProposal != null || session.HasPendingEdit
            || session.AutoLayoutParent(selected) is not { AutoLayout: { } parentLayout } parent || !session.CanEditAutoLayout(parent))
            return false;
        var child = session.LayoutElements(parent).FirstOrDefault(unit => unit.Layer == selected || unit.Companions.Contains(selected));
        if (child == null || !child.Bounds.Contains(pressDocument)) return false;
        bool row = parentLayout.Direction == LayoutDirection.Row;
        var offset = row ? child.Bounds.MidX - pressDocument.X : child.Bounds.MidY - pressDocument.Y;
        layoutGesture = (parent.Id, child.Layer.Id, 0, row, offset);
        layoutDropIndex = -1;
        drag = Drag.LayoutReorder;
        return true;
    }

    private void UpdateLayoutGesture(Point point)
    {
        if (session == null || layoutGesture is not { } gesture || session.Document.Find(gesture.Group) is not { } group) return;
        if (Distance(pressScreen, point) <= 2 && layoutDropIndex == -1 && !session.HasPendingEdit) return;
        if (drag == Drag.LayoutGap)
        {
            if (!session.HasPendingEdit && !session.BeginLayoutGap(group)) return;
            var delta = ToDocument(point) - pressDocument;
            session.PreviewLayoutGap(group, Math.Round(gesture.Gap + (gesture.Row ? delta.X : delta.Y)));
        }
        else
        {
            var docPoint = ToDocument(point);
            var axis = (gesture.Row ? docPoint.X : docPoint.Y) + gesture.GrabOffset;
            layoutDropIndex = session.LayoutElements(group).Count(unit => unit.Layer.Id != gesture.Child
                && (gesture.Row ? unit.Bounds.MidX : unit.Bounds.MidY) < axis);
        }
    }

    private void EndLayoutGesture(bool keep)
    {
        var gesture = layoutGesture;
        layoutGesture = null;
        var index = layoutDropIndex;
        layoutDropIndex = -1;
        if (session == null || gesture is not { } value || session.Document.Find(value.Group) is not { } group) return;
        if (value.Child == Guid.Empty)
        {
            if (session.HasPendingEdit) session.FinishLayoutGap(group, keep);
            else if (keep && layoutGapIndex >= 0) OpenLayoutGapEditor(group);
        }
        else if (keep && index >= 0) session.ReorderAutoLayout(group, value.Child, index);
    }

    private void OpenLayoutGapEditor(Layer group)
    {
        if (session is not { } target || group.AutoLayout is not { } layout || OverlayLayer.GetOverlayLayer(this) is not { } overlay) return;
        CloseLayoutGapEditor();
        var elements = target.LayoutElements(group);
        if (layoutGapIndex >= elements.Count - 1) return;
        var (_, label) = GapGeometry(group, elements, layoutGapIndex);
        const int editorWidth = 77;
        var position = new Point(Math.Clamp(label.Left, 0, Math.Max(0, Bounds.Width - editorWidth)), label.Top);
        if (this.TranslatePoint(position, overlay) is not { } anchor) return;
        var input = new TextBox { Text = layout.Gap.ToString("0.##"), Width = editorWidth, MinHeight = 25, Padding = new Thickness(6, 2),
            Margin = new Thickness(anchor.X, anchor.Y, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            BorderBrush = Palette.Accent, CornerRadius = new CornerRadius(5), Name = "CanvasLayoutGapInput" };
        Avalonia.Automation.AutomationProperties.SetName(input, Locale.T("Between elements"));
        var host = new Panel { Children = { input } };
        layoutGapEditor = (overlay, host);
        overlay.Children.Add(host);
        input.LostFocus += (_, _) => CloseLayoutGapEditor();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseLayoutGapEditor(); Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                if (session == target && target.ActiveLayer == group && group.AutoLayout is { } live && double.TryParse(input.Text, out var gap) && double.IsFinite(gap))
                    target.SetAutoLayout(group, live with { Gap = gap });
                CloseLayoutGapEditor();
                Focus();
                e.Handled = true;
            }
        };
        Dispatcher.UIThread.Post(() => { if (layoutGapEditor?.Host == host) { input.Focus(); input.SelectAll(); } }, DispatcherPriority.Input);
    }

    private void CloseLayoutGapEditor()
    {
        var opened = layoutGapEditor;
        layoutGapEditor = null;
        if (opened is { } editor) editor.Overlay.Children.Remove(editor.Host);
    }

    private void CaptureAutoLayoutOverlay(List<Action<SKCanvas>> steps, SKMatrix view, float hair)
    {
        if (session?.Tool != Tool.Move) return;
        if (layoutProposal is { } proposal)
        {
            var boxes = proposal.Boxes.Select(box => view.MapRect(box.Proposed)).ToArray();
            steps.Add(canvas =>
            {
                using var outline = new SKPaint { Color = Accent, StrokeWidth = hair, Style = SKPaintStyle.Stroke, IsAntialias = true,
                    PathEffect = SKPathEffect.CreateDash([5, 3], 0) };
                foreach (var box in boxes) canvas.DrawRect(box, outline);
            });
            return;
        }
        var group = drag == Drag.LayoutReorder && layoutGesture is { } reorder ? session.Document.Find(reorder.Group) : SelectedLayoutGroup();
        if (group?.AutoLayout is not { } layout) return;
        if (drag is not (Drag.None or Drag.LayoutGap or Drag.LayoutReorder) && session.Transform?.IsMovingAutoLayout != true) return;
        var elements = session.LayoutElements(group);
        if (elements.Count == 0) return;
        var bounds = view.MapRect(EditorSession.LayoutBounds(group));
        steps.Add(canvas =>
        {
            using var outline = new SKPaint { Color = Accent, StrokeWidth = hair, Style = SKPaintStyle.Stroke, IsAntialias = true };
            canvas.DrawRect(bounds, outline);
        });
        if (layoutDropIndex >= 0 && layoutGesture is { } gesture)
        {
            var rest = elements.Where(unit => unit.Layer.Id != gesture.Child).ToArray();
            if (rest.Length == 0) return;
            var previous = layoutDropIndex > 0 ? view.MapRect(rest[layoutDropIndex - 1].Bounds) : (SKRect?)null;
            var next = layoutDropIndex < rest.Length ? view.MapRect(rest[layoutDropIndex].Bounds) : (SKRect?)null;
            var leading = gesture.Row ? next?.Left : next?.Top;
            var trailing = gesture.Row ? previous?.Right : previous?.Bottom;
            var axis = leading is { } left && trailing is { } right ? (left + right) / 2 : leading ?? trailing.GetValueOrDefault();
            steps.Add(canvas =>
            {
                using var paint = new SKPaint { Color = Accent, StrokeWidth = 2 * hair, IsAntialias = true };
                if (gesture.Row) canvas.DrawLine(axis, bounds.Top - 4, axis, bounds.Bottom + 4, paint);
                else canvas.DrawLine(bounds.Left - 4, axis, bounds.Right + 4, axis, paint);
            });
            return;
        }
        if (drag == Drag.Transform || layoutGapIndex < 0 || layoutGapIndex >= elements.Count - 1 || !cursorInside) return;
        var (band, label) = GapGeometry(group, elements, layoutGapIndex);
        var text = $"{layout.Gap:0.##} px";
        steps.Add(canvas =>
        {
            using var fill = new SKPaint { Color = Accent.WithAlpha(60), IsAntialias = true };
            canvas.DrawRect(ToSkRect(band), fill);
            fill.Color = new SKColor(0x16, 0x65, 0xAF);
            canvas.DrawRoundRect(ToSkRect(label), 4, 4, fill);
            using var font = new SKFont(SKTypeface.Default, 11);
            fill.Color = SKColors.White;
            canvas.DrawText(text, (float)(label.Center.X - font.MeasureText(text) / 2), (float)label.Center.Y + 4, font, fill);
        });
    }

    private static SKRect ToSkRect(Rect area) => new((float)area.Left, (float)area.Top, (float)area.Right, (float)area.Bottom);
}
