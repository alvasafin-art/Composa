using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Composa.Editing;
using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.App.Controls;

/// <summary>The Type tool: typing straight onto the canvas, with the caret, selection and box handles drawn over the layer.</summary>
public sealed partial class CanvasView
{
    private TransformHandle textHandle;
    private (double Width, double Height) textBoxStart;
    private LayerTransform? textResizeStart;
    private SKPoint textResizeOffset;

    /// <summary>Raised when text editing starts or ends, so the options bar can swap its buttons.</summary>
    public event Action? TextEditingChanged;

    /// <summary>A document point in the coordinates of a text layer's pixels, which are also its layout's.</summary>
    private static SKPoint LocalTextPoint(Layer layer, SKPoint document) =>
        layer.Matrix.TryInvert(out var inverse) ? inverse.MapPoint(document) : document;

    private static bool InsideLayer(Layer layer, SKPoint document)
    {
        if (layer.Pixels == null) return false;
        var local = LocalTextPoint(layer, document);
        return local.X >= 0 && local.Y >= 0 && local.X <= layer.Pixels.Width && local.Y <= layer.Pixels.Height;
    }

    private SKPoint[]? TextBoxCorners()
    {
        if (session?.TextEditLayer is not { Pixels: { } pixels, Text.Path: null } layer) return null;
        return layer.Transform.Corners(pixels.Width, pixels.Height);
    }

    /// <summary>A press with the Type tool: a handle resizes the box, a click in the text places the caret, elsewhere starts new text.</summary>
    private void BeginTextPress(bool shift, int clicks)
    {
        if (session == null) return;
        if (session.TextEdit is { } editor && session.TextEditLayer is { Pixels: { } pixels } editing)
        {
            if (editor.Style.Path is { } path && HitTextPathStart(editing, editor.Layout, path, pressDocument))
            {
                drag = Drag.TextPathStart;
                return;
            }
            var corners = TextBoxCorners();
            textHandle = corners != null ? HitFrame(corners, pressScreen, allowRotate: false) : TransformHandle.None;
            if (corners != null && textHandle is not (TransformHandle.None or TransformHandle.Move))
            {
                var layout = editor.Layout;
                textBoxStart = (editor.Style.BoxWidth ?? layout.Width, editor.Style.BoxHeight ?? layout.Height);
                textResizeStart = editing.Transform;
                SKPoint Mid(int i, int j) => new((corners[i].X + corners[j].X) / 2, (corners[i].Y + corners[j].Y) / 2);
                var grab = textHandle switch
                {
                    TransformHandle.TopLeft => corners[0], TransformHandle.TopRight => corners[1],
                    TransformHandle.BottomRight => corners[2], TransformHandle.BottomLeft => corners[3],
                    TransformHandle.Top => Mid(0, 1), TransformHandle.Right => Mid(1, 2),
                    TransformHandle.Bottom => Mid(2, 3), _ => Mid(3, 0)
                };
                textResizeOffset = grab - pressDocument;
                drag = Drag.TextResize;
                return;
            }
            if (InsideLayer(editing, pressDocument) && (editor.Style.Path == null ||
                TextCurveParameter(editing, editor.Layout, editor.Style.Path, pressDocument, 12 / UnitsPerPixel) != null ||
                session.TextLayerAt(pressDocument) == editing))
            {
                var local = LocalTextPoint(editing, pressDocument);
                if (clicks >= 2) editor.SelectWordAt(editor.Layout.IndexAt(local));
                else editor.ClickAt(local, shift);
                drag = Drag.TextSelect;
                return;
            }
            session.FinishText();
            TextEditingChanged?.Invoke();
        }
        if (session.TextLayerAt(pressDocument) is { } layer && session.EditText(layer) is { } opened)
        {
            opened.ClickAt(LocalTextPoint(layer, pressDocument), shift);
            drag = Drag.TextSelect;
            TextEditingChanged?.Invoke();
            return;
        }
        if (session.Tool == Tool.TextOnPath)
        {
            if (session.TextPathSourceAt(pressDocument, (float)(9 / UnitsPerPixel)) is { } hit &&
                session.BeginTextOnPath(hit.Layer, hit.Start) != null)
            {
                Focus();
                TextEditingChanged?.Invoke();
            }
            return;
        }
        drag = Drag.TextBox;
    }

    private bool HitTextPathStart(Layer layer, TextLayout layout, TextPath path, SKPoint document) =>
        SKPoint.Distance(layer.Matrix.MapPoint(path.StartPoint() - layout.PathOrigin), document) * UnitsPerPixel <= 9;

    private static double? TextCurveParameter(Layer layer, TextLayout layout, TextPath path, SKPoint document, double tolerance)
    {
        var toDocument = SKMatrix.CreateTranslation(-layout.PathOrigin.X, -layout.PathOrigin.Y).PostConcat(layer.Matrix);
        var (segment, t) = path.Outline.NearestSegment(toDocument, document, tolerance);
        return segment >= 0 ? segment + t : null;
    }

    private void SetTextPathStart(SKPoint document)
    {
        if (session?.TextEdit is not { Style.Path: { } path } editor || session.TextEditLayer is not { } layer ||
            TextCurveParameter(layer, editor.Layout, path, document, double.PositiveInfinity) is not { } parameter ||
            SKPoint.Distance(document, pressDocument) * UnitsPerPixel < 2) return;
        editor.ChangeStyle(style => style with { Path = path.WithStart(parameter) });
    }

    /// <summary>A double-click with the Move tool: opens the topmost live text under the pointer for typing, the caret at the click.</summary>
    private bool BeginLiveTextEdit(bool shift)
    {
        if (session?.TextLayerAt(pressDocument) is not { } layer || session.EditText(layer) is not { } editor) return false;
        editor.ClickAt(LocalTextPoint(layer, pressDocument), shift);
        drag = Drag.TextSelect;
        UpdateCursor();
        TextEditingChanged?.Invoke();
        return true;
    }

    private void DragTextSelection()
    {
        if (session?.TextEdit is not { } editor || session.TextEditLayer is not { } layer) return;
        editor.MoveTo(editor.Layout.IndexAt(LocalTextPoint(layer, currentDocument)), select: true);
    }

    /// <summary>Drags one edge or corner of the box; the opposite edge stays where it is.</summary>
    private void DragTextBox(bool control)
    {
        if (session?.TextEdit == null || session.TextEditLayer is not { Pixels: { } pixels } layer || textResizeStart is not { } start) return;
        // Work in the box's own unrotated space, in layout pixels.
        var unrotate = SKMatrix.CreateRotationDegrees((float)-start.Rotation, start.Center.X, start.Center.Y);
        var from = unrotate.MapPoint(pressDocument);
        guides.Clear();
        SKPoint? direction = textHandle switch
        {
            TransformHandle.Left or TransformHandle.Right => new SKPoint(1, 0),
            TransformHandle.Top or TransformHandle.Bottom => new SKPoint(0, 1),
            _ => null
        };
        if (direction is { } axis) direction = SKMatrix.CreateRotationDegrees((float)start.Rotation).MapPoint(axis);
        var target = currentDocument;
        if (!control)
        {
            var grab = target + textResizeOffset;
            target += SnapLayoutHandle(grab, direction) - grab;
        }
        var to = unrotate.MapPoint(target);
        double scaleX = start.Width / textBoxStart.Width, scaleY = start.Height / textBoxStart.Height;
        double dx = (to.X - from.X) / Math.Max(1e-6, scaleX), dy = (to.Y - from.Y) / Math.Max(1e-6, scaleY);
        if (start.FlipHorizontal) dx = -dx;
        if (start.FlipVertical) dy = -dy;
        var movesLeft = textHandle is TransformHandle.TopLeft or TransformHandle.Left or TransformHandle.BottomLeft;
        var movesRight = textHandle is TransformHandle.TopRight or TransformHandle.Right or TransformHandle.BottomRight;
        var movesTop = textHandle is TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight;
        var movesBottom = textHandle is TransformHandle.BottomLeft or TransformHandle.Bottom or TransformHandle.BottomRight;
        var width = textBoxStart.Width + (movesRight ? dx : movesLeft ? -dx : 0);
        var height = textBoxStart.Height + (movesBottom ? dy : movesTop ? -dy : 0);
        session.SetTextBox(Math.Max(TextStyle.MinBox, width), Math.Max(TextStyle.MinBox, height), movesLeft ? 1 : 0, movesTop ? 1 : 0);
        _ = pixels;
        _ = layer;
    }

    private void FinishTextBoxDrag(bool moved)
    {
        if (session == null) return;
        bool vertical = session.Tool == Tool.VerticalText;
        if (moved) session.BeginText(MarqueeRect(false, false), vertical);
        else session.BeginText(pressDocument, vertical);
        Focus();
        TextEditingChanged?.Invoke();
    }

    /// <summary>Starts typing in an existing text layer (from the Layers panel or the Edit Text command).</summary>
    public void EditText(Layer layer)
    {
        if (session?.EditText(layer) is not { } editor) return;
        editor.MoveToDocumentEdge(end: true, select: false);
        Focus();
        InvalidateCanvas();
        TextEditingChanged?.Invoke();
    }

    /// <summary>
    /// Keys while text is being typed. Editing keys are acted on here. A key that may produce a character is left
    /// unhandled on purpose: the X11 backend only sends text input for a key press nobody handled, so marking it
    /// handled would swallow the letter. The window keeps tool shortcuts off while text is open instead.
    /// </summary>
    private bool HandleTextKey(TextEditor editor, KeyEventArgs e)
    {
        if (session == null) return false;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var step = shift ? 10 : 1;
        switch (e.Key)
        {
            case Key.Escape:
                if (drag != Drag.None) { drag = Drag.None; InvalidateCanvas(); return true; }
                session.CancelText();
                TextEditingChanged?.Invoke();
                return true;
            case Key.Enter or Key.Return when control:
                session.FinishText();
                TextEditingChanged?.Invoke();
                return true;
            case Key.Enter or Key.Return when editor.Style.Path != null:
                session?.FinishText();
                TextEditingChanged?.Invoke();
                return true;
            case Key.Enter or Key.Return: editor.Insert(shift ? "\u2028" : "\n"); return true;
            case Key.Tab: editor.Insert("\t"); return true;
            case Key.Back: editor.Backspace(word: control); return true;
            case Key.Delete: editor.Delete(word: control); return true;
            // Alt with the arrows sets spacing, as in Photoshop: left and right the tracking, up and down the leading.
            case Key.Left when alt: session.ChangeTextStyle(s => s with { Tracking = s.Tracking - step }); return true;
            case Key.Right when alt: session.ChangeTextStyle(s => s with { Tracking = s.Tracking + step }); return true;
            case Key.Up when alt: session.ChangeTextStyle(s => s with { Leading = Math.Max(1, s.LineHeight - step) }); return true;
            case Key.Down when alt: session.ChangeTextStyle(s => s with { Leading = s.LineHeight + step }); return true;
            case Key.Up when editor.Style.IsVertical: editor.MoveHorizontal(-1, shift, control); return true;
            case Key.Down when editor.Style.IsVertical: editor.MoveHorizontal(1, shift, control); return true;
            case Key.Left when editor.Style.IsVertical: editor.MoveVertical(1, shift); return true;
            case Key.Right when editor.Style.IsVertical: editor.MoveVertical(-1, shift); return true;
            case Key.Left: editor.MoveHorizontal(-1, shift, control); return true;
            case Key.Right: editor.MoveHorizontal(1, shift, control); return true;
            case Key.Up: editor.MoveVertical(-1, shift); return true;
            case Key.Down: editor.MoveVertical(1, shift); return true;
            case Key.Home: if (control) editor.MoveToDocumentEdge(false, shift); else editor.MoveToLineEdge(false, shift); return true;
            case Key.End: if (control) editor.MoveToDocumentEdge(true, shift); else editor.MoveToLineEdge(true, shift); return true;
            case Key.A when control: editor.SelectAll(); return true;
            case Key.Z when control && shift: editor.Redo(); return true;
            case Key.Z when control: editor.Undo(); return true;
            case Key.Y when control: editor.Redo(); return true;
            case Key.C when control: _ = CopyText(editor, cut: false); return true;
            case Key.X when control: _ = CopyText(editor, cut: true); return true;
            case Key.V when control: _ = PasteText(editor); return true;
        }
        // Ctrl and Alt combinations that mean nothing here are swallowed rather than reaching the menus. Everything
        // else (letters, digits, space, punctuation, dead keys) is left for the text input that follows it.
        return control || alt;
    }

    private async Task CopyText(TextEditor editor, bool cut)
    {
        if (!editor.HasSelection) return;
        var text = editor.SelectedText;
        if (cut) editor.Insert("");
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try { await clipboard.SetTextAsync(text); }
            catch { /* The desktop clipboard is a convenience; the edit itself has already happened. */ }
        }
    }

    private async Task PasteText(TextEditor editor)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        string? text = null;
        try { text = await clipboard.TryGetTextAsync(); }
        catch { /* Nothing usable on the clipboard. */ }
        if (!string.IsNullOrEmpty(text) && session?.TextEdit == editor) editor.Insert(text);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (session?.TextEdit is not { } editor || string.IsNullOrEmpty(e.Text)) return;
        var text = new string(e.Text.Where(c => c >= ' ' || c == '\n').ToArray());
        if (text.Length == 0) return;
        editor.Insert(text);
        e.Handled = true;
    }

    private void UpdateTextCursor(Point position)
    {
        if (controlHover) return; // Ctrl is held: the move cursor is showing what a drag would do.
        var type = StandardCursorType.Ibeam;
        if (session?.TextEdit != null && TextBoxCorners() is { } corners)
            type = HitFrame(corners, position, allowRotate: false) switch
            {
                TransformHandle.TopLeft or TransformHandle.BottomRight => StandardCursorType.TopLeftCorner,
                TransformHandle.TopRight or TransformHandle.BottomLeft => StandardCursorType.TopRightCorner,
                TransformHandle.Top or TransformHandle.Bottom => StandardCursorType.SizeNorthSouth,
                TransformHandle.Left or TransformHandle.Right => StandardCursorType.SizeWestEast,
                _ => StandardCursorType.Ibeam
            };
        if (session?.TextEdit is { Style.Path: { } path } editor && session.TextEditLayer is { } layer &&
            HitTextPathStart(layer, editor.Layout, path, ToDocument(position))) type = StandardCursorType.SizeAll;
        Cursor = new Cursor(type);
    }

    /// <summary>The box, its handles, the selection and the caret over the text being typed, plus a new box being dragged out.</summary>
    private void CaptureTextOverlay(List<Action<SKCanvas>> steps, SKMatrix view, float hair)
    {
        if (session == null) return;
        if (drag == Drag.TextBox)
        {
            var rect = view.MapRect(MarqueeRect(false, false));
            steps.Add(canvas =>
            {
                using var line = new SKPaint { Color = Accent, StrokeWidth = hair, Style = SKPaintStyle.Stroke };
                canvas.DrawRect(rect, line);
            });
        }
        if (session.TextEdit is not { } editor || session.TextEditLayer is not { Pixels: { } pixels } layer) return;
        var toView = layer.Matrix.PostConcat(view);
        var corners = layer.Transform.Corners(pixels.Width, pixels.Height).Select(p => view.MapPoint(p)).ToArray();
        var layout = editor.Layout;
        var selection = editor.HasSelection ? layout.SelectionPolygons(editor.SelectionStart, editor.SelectionEnd) : [];
        var caret = editor.HasSelection || antsPhase % 8 >= 4 ? null : layout.CaretSegmentAt(editor.Caret);
        var overflows = layout.Overflows;
        SKPoint? startMarker = null;
        if (editor.Style.Path is { } path)
        {
            if (drag == Drag.TextPathStart && TextCurveParameter(layer, layout, path, currentDocument, double.PositiveInfinity) is { } parameter)
                path = path.WithStart(parameter);
            startMarker = toView.MapPoint(path.StartPoint() - layout.PathOrigin);
        }
        steps.Add(canvas =>
        {
            using var line = new SKPaint { Color = Accent, StrokeWidth = hair, Style = SKPaintStyle.Stroke, IsAntialias = true };
            if (layout.CreateLocalTextPath(fullOutline: true) is { } curve)
            {
                using (curve)
                {
                    curve.Transform(toView);
                    canvas.DrawPath(curve, line);
                }
            }
            else
            {
                using var box = new SKPath();
                box.AddPoly(corners, close: true);
                canvas.DrawPath(box, line);
                DrawHandles(canvas, corners, hair);
            }
            if (startMarker is { } marker)
            {
                using var fill = new SKPaint { Color = Accent, IsAntialias = true };
                canvas.DrawCircle(marker, 4, fill);
                fill.Color = SKColors.White;
                canvas.DrawCircle(marker, 1.5f, fill);
            }
            if (overflows)
            {
                // Text that doesn't fit is marked by a plus in the bottom-right handle, as in Photoshop.
                using var dark = new SKPaint { Color = SKColors.Black, StrokeWidth = hair * 1.5f, IsAntialias = true };
                var p = corners[2];
                canvas.DrawLine(p.X - 2.5f, p.Y, p.X + 2.5f, p.Y, dark);
                canvas.DrawLine(p.X, p.Y - 2.5f, p.X, p.Y + 2.5f, dark);
            }
            if (selection.Count > 0)
            {
                using var fill = new SKPaint { Color = Accent.WithAlpha(90), IsAntialias = true };
                foreach (var polygon in selection)
                {
                    using var quad = new SKPath();
                    quad.AddPoly(polygon.Select(p => toView.MapPoint(p)).ToArray(), close: true);
                    canvas.DrawPath(quad, fill);
                }
            }
            if (caret is { } c)
            {
                var (top, bottom) = c;
                using var pen = new SKPaint { Color = Accent, StrokeWidth = Math.Max(hair * 1.5f, 1.5f), IsAntialias = true };
                using var halo = new SKPaint { Color = SKColors.White.WithAlpha(160), StrokeWidth = Math.Max(hair * 3.5f, 3.5f), IsAntialias = true };
                canvas.DrawLine(toView.MapPoint(top), toView.MapPoint(bottom), halo);
                canvas.DrawLine(toView.MapPoint(top), toView.MapPoint(bottom), pen);
            }
        });
    }
}
