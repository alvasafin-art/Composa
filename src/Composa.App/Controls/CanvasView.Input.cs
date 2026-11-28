using Avalonia;
using TopLevel = Avalonia.Controls.TopLevel;
using Avalonia.Input;
using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    private enum Drag { None, Pan, Marquee, MoveSelection, MovePixels, Lasso, SelectionBrush, Crop, Stroke, Gradient, Shape, Transform, Eyedropper, ZoomScrub, TextBox, TextSelect, TextResize, TextPathStart, Guide, AiRectangle, PromptRegion, Patch, PenNode, PenLasso, PenErase, WarpNode, ShapeParameter, LayoutGap, LayoutReorder }
    private bool brushPreparationRequested;
    public Func<bool>? AiToolsAvailable { get; set; }
    public event Action<Composa.AI.AiTaskKind>? AiSelectionCompleted;
    public event Action<SKRectI, SelectionMode>? AiObjectSelectionRequested;
    public event Action<int, int, SelectionMode>? ObjectSelectionPointRequested;
    public Func<bool>? ObjectSelectionAvailable { get; set; }

    private Drag drag;
    private MouseButton dragButton;
    private bool recapture;

    /// <summary>True while the pointer is dragging out an edit; commands wait until it finishes.</summary>
    public bool IsDragging => drag is not (Drag.None or Drag.Pan) && !(drag == Drag.Lasso && session?.LassoKind == LassoKind.Polygonal);
    private Point pressScreen, cursorScreen;
    private SKPoint pressDocument, currentDocument;
    private bool cursorInside;
    private SelectionMode dragMode;
    private KeyModifiers dragModifiers;
    /// <summary>Shift was let go during a marquee drag, so pressing it again squares the marquee (see <see cref="ConstrainsMarquee"/>).</summary>
    private bool shiftReleased;
    /// <summary>A Ctrl-drag with another tool is moving layers: Photoshop's temporary Move tool.</summary>
    private bool temporaryMove;
    /// <summary>Ctrl is held over the canvas with another tool, so the cursor promises a move.</summary>
    private bool controlHover;
    private Guid? moveGroupScope;
    private Guid? moveHover;
    private Guid? pendingAlignmentAnchor;
    private bool pendingMoveDoubleClick;
    private Point pressTopLevel;
    private bool altHover;
    private bool useEyedropperCursor;
    private readonly List<SKPoint> polygon = [];
    private SKRect? cropRect;
    private bool cropFramePristine;
    private SKRect cropStart;
    private TransformHandle handle;
    private int distortCorner = -1;
    private bool distortEdge;
    private bool IsResizeDrag => UpdatesTransformModifiers && distortCorner < 0 && !distortEdge;
    private bool UpdatesTransformModifiers => drag == Drag.Transform &&
        handle is not (TransformHandle.None or TransformHandle.Move or TransformHandle.Rotate);
    private bool spaceDown;
    private double scrubZoom;
    private readonly List<(SKPoint From, SKPoint To)> guides = [];
    /// <summary>Where a marquee or shape starts and where its corner is now, after snapping; the press and pointer positions otherwise.</summary>
    private SKPoint snapFrom, snapTo;
    /// <summary>How far a selection outline being dragged has moved, after Shift's axis lock and snapping, and the outline's bounds when the drag began.</summary>
    private SKPointI selectionOffset;
    private SKRect selectionBox;
    private Layer? gradientLayer;
    private SKBitmap? gradientOriginal;
    // A drawn gradient stays adjustable (drag either end) until Enter, Escape, or another action settles it.
    private SKPoint gradientFrom, gradientTo;
    private bool gradientPending, gradientMovesStart;

    /// <summary>The gradient is still open only while the session's edit is; any other command will have committed it.</summary>
    private bool HasPendingGradient => gradientPending && session is { IsInteracting: true } && gradientLayer != null && gradientOriginal != null;

    private void SettleGradient(bool keep)
    {
        if (HasPendingGradient) { if (keep) session!.Commit(); else session!.Cancel(); }
        gradientPending = false;
        gradientOriginal = null;
        gradientLayer = null;
        InvalidateCanvas();
    }

    public bool HasCrop => cropRect != null;
    public SKRect? CropRect => cropRect;

    public void ResetCropToCanvas()
    {
        if (session?.Tool != Tool.Crop) return;
        cropRect = SKRect.Create(0, 0, session.Document.Width, session.Document.Height);
        cropFramePristine = true;
        Fit();
        ToolStateChanged?.Invoke();
    }

    private bool IsBrushTool => session?.Tool is Tool.Brush or Tool.Eraser or Tool.SpotHealing or Tool.HealingBrush or Tool.CloneStamp or Tool.Smear or Tool.SelectionBrush or Tool.RemoveObject;

    public void ToolChanged()
    {
        transformSelection = null;
        if (session?.Tool != Tool.PromptRegion) activePromptRegion = null;
        if (session?.Tool != Tool.Pen)
        {
            FinishPen();
            PenMode = PenEditMode.Draw;
        }
        CancelInteraction();
        if (session?.Tool != Tool.Crop) { cropRect = null; cropFramePristine = false; }
        // With a selection, the crop starts at its bounds, as Photoshop's does: C, then Enter, crops to it.
        else if (cropRect == null && session.Selection is { } selection && SelectionMask.Bounds(selection) is { IsEmpty: false } bounds)
            cropRect = ConstrainCrop(new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        else if (cropRect == null && session != null)
        { cropRect = ConstrainCrop(SKRect.Create(0, 0, session.Document.Width, session.Document.Height)); cropFramePristine = true; }
        UpdateCursor();
        InvalidateCanvas();
    }

    /// <summary>The crop bar's ratio changed: the box, when there is one, is reshaped around its center to match.</summary>
    public void ChangeCropRatio()
    {
        if (cropRect is { } crop) cropRect = ConstrainCrop(cropFramePristine && session != null
            ? SKRect.Create(0, 0, session.Document.Width, session.Document.Height) : crop);
        InvalidateCanvas();
        ToolStateChanged?.Invoke();
    }

    /// <summary>Reshapes a box to the chosen ratio about its center, kept to the width or height that still fits the canvas.</summary>
    private SKRect ConstrainCrop(SKRect box)
    {
        if (session?.CropAspect is not { } aspect || box.Width < 1 || box.Height < 1) return box;
        var width = box.Width;
        var height = (float)(width / aspect);
        if (height > session.Document.Height) { height = session.Document.Height; width = (float)(height * aspect); }
        if (width > session.Document.Width) { width = session.Document.Width; height = (float)(width / aspect); }
        var shaped = SKRect.Create(box.MidX - width / 2, box.MidY - height / 2, width, height);
        // Slid back inside the canvas when the reshaping pushed it out.
        var dx = Math.Max(0, -shaped.Left) - Math.Max(0, shaped.Right - session.Document.Width);
        var dy = Math.Max(0, -shaped.Top) - Math.Max(0, shaped.Bottom - session.Document.Height);
        shaped.Offset(dx, dy);
        return shaped;
    }

    public void CancelInteraction()
    {
        CloseLayoutGapEditor();
        if (session == null) { drag = Drag.None; return; }
        switch (drag)
        {
            case Drag.PromptRegion: break; // Geometry follows currentDocument; commit once on release.
            case Drag.LayoutGap or Drag.LayoutReorder: EndLayoutGesture(keep: false); break;
            case Drag.ShapeParameter: session.Cancel(); adjustingShape = null; break;
            case Drag.WarpNode: EndWarpPoint(keep: false); break;
            case Drag.TextPathStart: session.TextEdit?.EndPathStartDrag(keep: false); break;
            case Drag.Stroke: session.CancelStroke(); break;
            case Drag.Patch: session.CancelPreview(); break;
            case Drag.PenNode: if (!penDraftDrag) session.Cancel(); penLayer = null; penOriginal = null; penCornerDrag = null; penDraft.Clear(); break;
            case Drag.PenErase: EndPenErase(keep: false); break;
            case Drag.SelectionBrush: session.CancelSelectionBrush(); break;
            case Drag.Transform: session.CancelTransform(); break;
            case Drag.MovePixels: session.EndMovePixels(keep: false); break;
            case Drag.Gradient: gradientPending = true; SettleGradient(keep: false); break;
        }
        if (drag != Drag.Gradient) SettleGradient(keep: true);
        // Text being typed stays open: only Escape, Ctrl+Enter or another action ends it.
        drag = Drag.None;
        temporaryMove = false;
        polygon.Clear();
        guides.Clear();
        InvalidateCanvas();
    }

    private void UpdateCursor()
    {
        var type = StandardCursorType.Arrow;
        useEyedropperCursor = false;
        if (session != null)
        {
            if (spaceDown || drag == Drag.Pan) type = StandardCursorType.Hand;
            else if (session.WarpEdit != null) type = StandardCursorType.Cross;
            else if (temporaryMove || (controlHover && drag == Drag.None && session.Tool != Tool.Pen)) type = StandardCursorType.SizeAll; // The four-way move arrow says what Ctrl will do.
            else if (session.ColorRange != null) type = StandardCursorType.Cross; // Every click picks a color while the panel is open.
            else if (session.Tool == Tool.Eyedropper || session.Tool == Tool.Brush && (drag == Drag.Eyedropper || drag == Drag.None && altHover))
            { useEyedropperCursor = true; type = StandardCursorType.Cross; }
            else type = session.Tool switch
            {
                Tool.Hand => StandardCursorType.Hand,
                Tool.PromptRegion => PromptRegionCursor(),
                Tool.Marquee or Tool.Lasso or Tool.Wand or Tool.Crop or Tool.Gradient or Tool.Shape or Tool.Bucket or Tool.Pen or Tool.Patch => StandardCursorType.Cross,
                Tool.Text or Tool.TextOnPath or Tool.VerticalText => StandardCursorType.Ibeam,
                Tool.Brush or Tool.Eraser or Tool.SpotHealing or Tool.HealingBrush or Tool.CloneStamp or Tool.Smear or Tool.SelectionBrush or Tool.RemoveObject => StandardCursorType.None,
                Tool.Zoom => StandardCursorType.Cross,
                _ => StandardCursorType.Arrow
            };
        }
        canvasCursorType = type;
        ApplyCanvasCursor(updateStandard: true);
    }

    private static SelectionMode ModeFor(KeyModifiers modifiers)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        return shift && alt ? SelectionMode.Intersect : shift ? SelectionMode.Add : alt ? SelectionMode.Subtract : SelectionMode.Replace;
    }

    private bool InsideSelection(SKPoint p)
    {
        var selection = session?.Selection;
        int x = (int)p.X, y = (int)p.Y;
        return selection != null && x >= 0 && y >= 0 && x < selection.Width && y < selection.Height && selection.GetPixel(x, y).Alpha >= 128;
    }

    // ---- Pointer ------------------------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (session == null || drag != Drag.None && !(drag == Drag.Lasso && session.LassoKind == LassoKind.Polygonal)) return;
        if (session.Tool == Tool.RemoveObject && AiToolsAvailable?.Invoke() != true)
        { Problem?.Invoke(Locale.T("Check the Remove Object connection in AI Settings and wait for the current operation to finish.")); return; }
        if (session.Tool == Tool.Wand && session.WandMode == WandMode.Object
            && ObjectSelectionAvailable?.Invoke() == false)
        { Problem?.Invoke(Locale.T("Wait for selection to finish or check the Object Selection connection in AI Settings.")); return; }
        var point = e.GetCurrentPoint(this);
        brushCursorPointer = e.Pointer.Type;
        pressScreen = cursorScreen = point.Position;
        pressTopLevel = e.GetPosition(TopLevel.GetTopLevel(this));
        pendingMoveDoubleClick = false;
        pressDocument = currentDocument = ToDocument(point.Position);
        dragButton = point.Properties.IsMiddleButtonPressed ? MouseButton.Middle : point.Properties.IsRightButtonPressed ? MouseButton.Right : MouseButton.Left;
        dragModifiers = e.KeyModifiers;
        e.Pointer.Capture(this);

        if (point.Properties.IsMiddleButtonPressed || spaceDown || (session.Tool == Tool.Hand && point.Properties.IsLeftButtonPressed))
        {
            drag = Drag.Pan;
            UpdateCursor();
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (session.WarpEdit != null) { BeginWarpPoint(point.Position); e.Handled = true; return; }
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // While Select > Color Range is open every click picks a color, whatever the tool: Shift adds it, Alt takes it away.
        if (session.ColorRange != null)
        {
            session.SampleColorRange((int)Math.Floor(pressDocument.X), (int)Math.Floor(pressDocument.Y), alt ? ColorRangeSample.Remove : shift ? ColorRangeSample.Add : null);
            e.Pointer.Capture(null);
            return;
        }

        snapFrom = snapTo = pressDocument;
        selectionOffset = SKPointI.Empty;
        // A press on a ruler drags a new guide out of it; with the Move tool a press on a guide moves it.
        if (BeginGuideDrag(point.Position)) { InvalidateCanvas(); return; }
        if (OverRuler(point.Position)) return;

        // Ctrl-drag moves the current layers whatever tool is chosen, as Photoshop's temporary Move tool does. Two
        // Ctrl presses already mean something and keep it: the Marquee's Ctrl-drag inside a selection moves its
        // pixels, and Ctrl on a crop frame's handles adjusts the crop without snapping.
        var movingPixels = session.Tool == Tool.Marquee && session.CanMovePixels && InsideSelection(pressDocument);
        var adjustingCrop = session.Tool == Tool.Crop && cropRect is { } cropFrame && HitFrame(Corners(cropFrame), point.Position, allowRotate: false) != TransformHandle.None;
        if (control && session.Tool is not (Tool.Move or Tool.Pen) && !movingPixels && !adjustingCrop) BeginTemporaryMove();
        else switch (session.Tool)
        {
            case Tool.Move:
                if (!control && BeginShapeParameter(point.Position, alt)) break;
                // A double-click on live text opens it for typing, without switching to the Type tool first.
                pendingMoveDoubleClick = e.ClickCount >= 2 && !shift;
                var moveHandle = ShowTransformControls && CurrentFrame() is { } moveFrame
                    ? HitFrame(moveFrame, pressScreen, allowRotate: true) : TransformHandle.None;
                if (!control && !shift && !alt && BeginLayoutGesture(point.Position, moveHandle is TransformHandle.None or TransformHandle.Move)) break;
                if ((transformSelection == null || moveHandle == TransformHandle.None) && session.CanMovePixels && InsideSelection(pressDocument) && session.BeginMovePixels(duplicate: alt)) drag = Drag.MovePixels;
                else BeginMove(control, e.ClickCount, moveHandle);
                break;
            case Tool.PromptRegion:
                BeginRegionDrag(point.Position, e.ClickCount);
                UpdateCursor();
                break;
            case Tool.Marquee:
                dragMode = ModeFor(e.KeyModifiers & ~KeyModifiers.Control);
                // Adding to no selection is replacing, so a Shift held from the start squares the marquee instead of choosing a mode.
                if (session.Selection == null && dragMode == SelectionMode.Add) dragMode = SelectionMode.Replace;
                shiftReleased = false;
                if (control && InsideSelection(pressDocument) && session.BeginMovePixels(duplicate: alt)) { drag = Drag.MovePixels; break; }
                drag = dragMode == SelectionMode.Replace && InsideSelection(pressDocument) ? Drag.MoveSelection : Drag.Marquee;
                if (drag == Drag.Marquee) snapFrom = snapTo = SnapCorner(pressDocument, control: false);
                break;
            case Tool.Lasso:
                dragMode = polygon.Count == 0 ? ModeFor(e.KeyModifiers) : dragMode;
                if (session.LassoKind == LassoKind.Freehand)
                {
                    if (dragMode == SelectionMode.Replace && InsideSelection(pressDocument)) { drag = Drag.MoveSelection; break; }
                    polygon.Clear();
                    polygon.Add(pressDocument);
                    drag = Drag.Lasso;
                }
                else
                {
                    drag = Drag.Lasso;
                    var closes = polygon.Count > 2 && Distance(ToScreen(polygon[0]), point.Position) < 8;
                    if (e.ClickCount >= 2 || closes) FinishPolygon();
                    else polygon.Add(pressDocument);
                }
                break;
            case Tool.Wand when session.WandMode == WandMode.Wand:
                session.SelectWand((int)Math.Floor(pressDocument.X), (int)Math.Floor(pressDocument.Y), ModeFor(e.KeyModifiers));
                break;
            case Tool.Wand:
                dragMode = ModeFor(e.KeyModifiers);
                drag = Drag.AiRectangle;
                snapFrom = snapTo = pressDocument;
                ToolStateChanged?.Invoke();
                break;
            case Tool.Bucket:
                session.BucketFill(pressDocument);
                break;
            case Tool.Patch:
                if (session.Selection is { } repair && pressDocument.X >= 0 && pressDocument.Y >= 0 && pressDocument.X < repair.Width && pressDocument.Y < repair.Height
                    && repair.GetPixel((int)pressDocument.X, (int)pressDocument.Y).Alpha > 0 && session.BeginPatch()) drag = Drag.Patch;
                else { drag = Drag.Marquee; dragMode = Composa.Selections.SelectionMode.Replace; snapFrom = snapTo = pressDocument; }
                break;
            case Tool.Pen:
                PenPress(alt, control, shift, e.ClickCount);
                break;
            case Tool.SelectionBrush or Tool.RemoveObject:
                var selectionMode = session.Tool == Tool.RemoveObject ? SelectionMode.Replace : alt ? SelectionMode.Subtract : shift ? SelectionMode.Add : session.SelectionBrushMode;
                session.BeginSelectionBrush(pressDocument, selectionMode);
                drag = Drag.SelectionBrush;
                break;
            case Tool.Crop:
                cropFramePristine = false;
                handle = cropRect is { } crop ? HitFrame(Corners(crop), point.Position, allowRotate: false) : TransformHandle.None;
                if (handle == TransformHandle.None) { cropRect = null; handle = TransformHandle.BottomRight; cropStart = SKRect.Create(Snap(pressDocument.X), Snap(pressDocument.Y), 0, 0); }
                else cropStart = cropRect!.Value;
                drag = Drag.Crop;
                break;
            case Tool.Brush or Tool.Eraser or Tool.SpotHealing or Tool.CloneStamp or Tool.HealingBrush or Tool.Smear:
                if (alt && session.Tool is Tool.CloneStamp or Tool.HealingBrush) { session.SetCloneSource(pressDocument); InvalidateCanvas(); break; }
                if (alt && session.Tool == Tool.Brush) { PickColor(background: false); drag = Drag.Eyedropper; break; }
                session.ViewZoom = UnitsPerPixel;
                if (session.BeginStroke(pressDocument, out var problem, lineFromLast: shift && session.LastStrokeEnd != null,
                    pressure: e.Pointer.Type == PointerType.Pen ? point.Properties.Pressure : 1)) drag = Drag.Stroke;
                else if (problem != null) Problem?.Invoke(problem);
                break;
            case Tool.Gradient:
                if (HasPendingGradient)
                {
                    var nearStart = Distance(ToScreen(gradientFrom), point.Position) <= 10;
                    if (nearStart || Distance(ToScreen(gradientTo), point.Position) <= 10)
                    {
                        gradientMovesStart = nearStart;
                        drag = Drag.Gradient;
                        break;
                    }
                }
                SettleGradient(keep: true);
                if (session.EditableLayer is not { } target) { Problem?.Invoke("Select a pixel layer or a mask to draw a gradient on."); break; }
                gradientMovesStart = false;
                gradientFrom = gradientTo = pressDocument;
                gradientOriginal = session.BeginGradient(target);
                gradientLayer = target;
                drag = Drag.Gradient;
                break;
            case Tool.Shape:
                if (!control && BeginShapeParameter(point.Position, alt)) break;
                if (TryEditSelectedPath(alt, shift, e.ClickCount)) break;
                penSelectedLayer = null;
                drag = Drag.Shape;
                snapFrom = snapTo = SnapCorner(pressDocument, control: false);
                break;
            case Tool.Text or Tool.TextOnPath or Tool.VerticalText:
                BeginTextPress(shift, e.ClickCount);
                break;
            case Tool.Eyedropper:
                PickColor(alt);
                drag = Drag.Eyedropper;
                break;
            case Tool.Zoom:
                drag = Drag.ZoomScrub;
                scrubZoom = zoom;
                break;
        }
        // The outline does not change while it is dragged, so its bounds are found once for snapping.
        if (drag == Drag.MoveSelection && session.Selection is { } outline) selectionBox = (SKRect)SelectionMask.Bounds(outline);
        if (drag == Drag.Eyedropper) UpdateCursor();
        else if (drag == Drag.Stroke) InvalidateBrushCursor();
        else InvalidateCanvas();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var delta = position - cursorScreen;
        cursorScreen = position;
        cursorInside = true;
        brushCursorPointer = e.Pointer.Type;
        if (session == null) return;
        QueueBrushPreparation();
        currentDocument = ToDocument(position);
        PointerAt?.Invoke(new SKPointI((int)Math.Floor(currentDocument.X), (int)Math.Floor(currentDocument.Y)));
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        snapTo = currentDocument;

        switch (drag)
        {
            case Drag.LayoutGap or Drag.LayoutReorder: UpdateLayoutGesture(position); break;
            case Drag.ShapeParameter: UpdateShapeParameter(); break;
            case Drag.Pan: PanBy(delta); break;
            case Drag.Patch: session.PreviewPatch(currentDocument - pressDocument); break;
            case Drag.PenNode: PenMove(alt, shift); break;
            case Drag.PenErase: ErasePenTo(currentDocument); break;
            case Drag.Marquee or Drag.Shape or Drag.AiRectangle: snapTo = SnapCorner(currentDocument, e.KeyModifiers.HasFlag(KeyModifiers.Control)); break;
            case Drag.MoveSelection: selectionOffset = SnappedSelectionOffset(shift, e.KeyModifiers.HasFlag(KeyModifiers.Control)); break;
            case Drag.Stroke:
                session.ViewZoom = UnitsPerPixel;
                ContinueBrushDrag(e);
                break;
            case Drag.SelectionBrush:
                foreach (var p in e.GetIntermediatePoints(this)) session.ContinueSelectionBrush(ToDocument(p.Position));
                break;
            case Drag.Lasso when session.LassoKind == LassoKind.Freehand:
            case Drag.PenLasso:
                if (polygon.Count == 0 || Distance(ToScreen(polygon[^1]), position) >= 2) polygon.Add(currentDocument);
                break;
            case Drag.Crop: DragCrop(shift, alt); break;
            case Drag.MovePixels:
                float mx = currentDocument.X - pressDocument.X, my = currentDocument.Y - pressDocument.Y;
                if (shift) { if (Math.Abs(mx) > Math.Abs(my)) my = 0; else mx = 0; }
                session.MovePixelsBy((int)Math.Round(mx), (int)Math.Round(my));
                break;
            case Drag.Transform: DragTransform(shift, alt, e.KeyModifiers.HasFlag(KeyModifiers.Control)); break;
            case Drag.WarpNode: DragWarpPoint(shift); break;
            case Drag.Gradient:
                if (gradientLayer == null || gradientOriginal == null) break;
                if (gradientMovesStart) gradientFrom = ConstrainAngle(gradientTo, currentDocument, shift);
                else gradientTo = ConstrainAngle(gradientFrom, currentDocument, shift);
                session.DrawGradient(gradientLayer, gradientOriginal, gradientFrom, gradientTo);
                break;
            case Drag.Eyedropper: PickColor(alt && session.Tool == Tool.Eyedropper); break;
            case Drag.ZoomScrub:
                if (Math.Abs(position.X - pressScreen.X) > 4)
                    ZoomTo(scrubZoom * Math.Pow(2, (position.X - pressScreen.X) / 120), pressScreen, rescale: true);
                break;
            case Drag.TextSelect: DragTextSelection(); break;
            case Drag.TextPathStart: DragTextPathStart(currentDocument); break;
            case Drag.TextResize: DragTextBox(e.KeyModifiers.HasFlag(KeyModifiers.Control)); break;
            case Drag.Guide: DragGuide(); break;
            case Drag.None when session.Tool == Tool.Move:
                UpdateMoveCursor(position);
                UpdateLayoutHover(position);
                UpdateMoveHover(position, e.KeyModifiers);
                break;
            case Drag.None when session.Tool is Tool.Text or Tool.TextOnPath or Tool.VerticalText:
                UpdateTextCursor(position);
                break;
        }
        if (session.Tool is Tool.Move or Tool.Shape || hoveredParameter >= 0) UpdateShapeHint(position);
        if (drag == Drag.None) SetModifierHover(e.KeyModifiers.HasFlag(KeyModifiers.Control), alt);
        if (session.Tool == Tool.PromptRegion && drag == Drag.None) UpdateCursor();
        if (drag == Drag.Marquee && !shift) shiftReleased = true;
        dragModifiers = e.KeyModifiers;
        // Pixel damage from ContinueStroke already invalidates the canvas. When a
        // sample only moves the cursor, keep the document frame that is on screen.
        if (IsBrushTool && drag is Drag.None or Drag.Stroke) InvalidateBrushCursor();
        // Idle cursor changes are handled by the cursor itself. The canvas only
        // needs a new overlay while a gesture or a polygonal outline is changing.
        else if (drag is not (Drag.None or Drag.Pan) || drag == Drag.None && polygon.Count > 0)
            InvalidateCanvas();
    }

    private void ContinueBrushDrag(PointerEventArgs e)
    {
        var points = e.GetIntermediatePoints(this);
        var isPen = e.Pointer.Type == PointerType.Pen;
        if (points.Count == 1)
        {
            var point = points[0];
            session!.ContinueStroke(ToDocument(point.Position), isPen ? point.Properties.Pressure : 1);
            return;
        }

        Span<(SKPoint Point, float Pressure)> samples = points.Count <= 64
            ? stackalloc (SKPoint, float)[points.Count] : new (SKPoint, float)[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            samples[i] = (ToDocument(point.Position), isPen ? point.Properties.Pressure : 1);
        }
        session!.ContinueStrokeSamples(samples);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Only the button that started a drag ends it; a stray right or middle click mid-stroke changes nothing.
        if (session == null) return;
        if (drag != Drag.None && e.InitialPressMouseButton != dragButton)
        {
            // Avalonia drops pointer capture on any button-up; the drag takes it back instead of being cancelled.
            recapture = true;
            return;
        }
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (drag is Drag.AiRectangle or Drag.Shape or Drag.ShapeParameter or Drag.PromptRegion)
        {
            // A fast drag can release beyond the last delivered move event.
            cursorScreen = e.GetPosition(this);
            currentDocument = ToDocument(cursorScreen);
            if (drag == Drag.ShapeParameter) UpdateShapeParameter();
            else if (drag != Drag.PromptRegion) snapTo = SnapCorner(currentDocument, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        }
        var releasePosition = e.GetPosition(this);
        var moved = Distance(pressTopLevel, e.GetPosition(TopLevel.GetTopLevel(this))) > 2;
        if (drag == Drag.Transform && moved && (releasePosition != cursorScreen ||
            UpdatesTransformModifiers && e.KeyModifiers != dragModifiers && Distance(pressScreen, releasePosition) > 2))
        {
            cursorScreen = releasePosition;
            currentDocument = ToDocument(cursorScreen);
            DragTransform(shift, alt, e.KeyModifiers.HasFlag(KeyModifiers.Control));
        }
        if (drag is Drag.PenNode or Drag.WarpNode or Drag.TextResize && e.GetPosition(this) != cursorScreen)
        {
            cursorScreen = e.GetPosition(this);
            currentDocument = ToDocument(cursorScreen);
            if (Distance(pressScreen, cursorScreen) > 2)
            {
                if (drag == Drag.PenNode) PenMove(alt, shift);
                else if (drag == Drag.WarpNode) DragWarpPoint(shift);
                else DragTextBox(e.KeyModifiers.HasFlag(KeyModifiers.Control));
            }
        }
        if (drag == Drag.Stroke && session.CurrentBrushMode is BrushMode.Paint or BrushMode.Erase
            && e.GetPosition(this) is var release && release != cursorScreen)
        {
            cursorScreen = release;
            currentDocument = ToDocument(release);
            // Button-up can carry a new position and zero pen pressure. Extend it
            // using the latest pressure received while the tip was still down.
            session.ContinueStroke(currentDocument, session.BrushPressure);
        }
        if (drag is Drag.LayoutGap or Drag.LayoutReorder) UpdateLayoutGesture(e.GetPosition(this));
        if (drag == Drag.PenErase) ErasePenTo(ToDocument(releasePosition));
        if (drag == Drag.PenLasso) polygon.Add(ToDocument(releasePosition));
        var finished = drag;
        if (!(drag == Drag.Lasso && session.LassoKind == LassoKind.Polygonal)) drag = Drag.None;
        var wasTemporaryMove = temporaryMove;
        temporaryMove = false;
        e.Pointer.Capture(null);

        switch (finished)
        {
            case Drag.LayoutGap or Drag.LayoutReorder: EndLayoutGesture(keep: true); break;
            case Drag.ShapeParameter:
                if (adjustingShape?.Shape != originalShape) session.Commit(); else session.Cancel();
                adjustingShape = null; break;
            case Drag.WarpNode: guides.Clear(); EndWarpPoint(keep: true); break;
            case Drag.Pan: UpdateCursor(); break;
            case Drag.Stroke: session.EndStroke(); QueueBrushPreparation(); break;
            case Drag.PenNode:
                guides.Clear();
                if (!penDraftDrag && penLayer != null)
                {
                    if (moved || penConverted) session.CommitPathPreview();
                    else session.Cancel();
                }
                penLayer = null; penOriginal = null; penCornerDrag = null; break;
            case Drag.PenLasso: EndPenLasso(shift, alt); break;
            case Drag.PenErase: EndPenErase(keep: true); break;
            case Drag.Patch:
                if (moved) { session.PreviewPatch(currentDocument - pressDocument, final: true); session.CommitPreview(); }
                else session.CancelPreview();
                break;
            case Drag.SelectionBrush:
                session.EndSelectionBrush();
                if (session.Tool == Tool.RemoveObject && session.Selection != null) AiSelectionCompleted?.Invoke(Composa.AI.AiTaskKind.RemoveObject);
                break;
            case Drag.PromptRegion:
                FinishRegionDrag();
                UpdateCursor();
                break;
            case Drag.AiRectangle:
                if (moved)
                {
                    var search = MarqueeRect(false, false);
                    var area = SKRectI.Intersect(session.Document.Bounds, new SKRectI((int)Math.Floor(search.Left), (int)Math.Floor(search.Top), (int)Math.Ceiling(search.Right), (int)Math.Ceiling(search.Bottom)));
                    // Box prompts support addition and subtraction. Alt wins when both modifiers are down.
                    if (!area.IsEmpty) AiObjectSelectionRequested?.Invoke(area,
                        dragMode == SelectionMode.Intersect ? SelectionMode.Subtract : dragMode);
                }
                else
                {
                    int x = (int)Math.Floor(pressDocument.X), y = (int)Math.Floor(pressDocument.Y);
                    if (ObjectSelectionPointRequested != null) ObjectSelectionPointRequested(x, y, dragMode);
                    else session.SelectObject(x, y, dragMode);
                }
                break;
            case Drag.Marquee:
                guides.Clear();
                if (!moved) { if (dragMode == SelectionMode.Replace) session.Deselect(); break; }
                var rect = MarqueeRect(ConstrainsMarquee(e.KeyModifiers), false);
                if (session.MarqueeKind == MarqueeKind.Ellipse) session.SelectEllipse(rect, dragMode); else session.SelectRect(rect, dragMode);
                break;
            case Drag.MoveSelection:
                guides.Clear();
                if (moved) session.MoveSelection(selectionOffset.X, selectionOffset.Y);
                else session.Deselect();
                break;
            case Drag.Lasso when session.LassoKind == LassoKind.Freehand:
                if (polygon.Count > 2) session.SelectPolygon(polygon.ToList(), dragMode);
                else if (dragMode == SelectionMode.Replace) session.Deselect();
                polygon.Clear();
                break;
            case Drag.Crop:
                if (cropRect is { } crop && (crop.Width < 1 || crop.Height < 1)) cropRect = null;
                ToolStateChanged?.Invoke();
                break;
            case Drag.MovePixels: session.EndMovePixels(keep: moved); break;
            case Drag.Transform:
                guides.Clear();
                if (moved) session.CommitTransform(); else session.CancelTransform();
                if (!moved && pendingAlignmentAnchor is { } anchor) session.SetAlignmentAnchor(anchor);
                break;
            case Drag.Gradient:
                // Released: the gradient stays open for adjustment. A click that drew nothing is dropped.
                if (moved || gradientPending) gradientPending = true;
                else { gradientPending = true; SettleGradient(keep: false); }
                ToolStateChanged?.Invoke();
                break;
            case Drag.Shape:
                guides.Clear();
                if (!moved) { CreateShapeRequested?.Invoke(snapFrom); break; }
                if (session.ShapeKind == ShapeKind.Line) session.AddLine(snapFrom, LineEnd(shift));
                else session.AddShape(MarqueeRect(shift, alt));
                break;
            case Drag.ZoomScrub:
                if (!moved) ZoomTo(alt ? zoom / 1.5 : zoom * 1.5, pressScreen);
                break;
            case Drag.TextBox: FinishTextBoxDrag(moved); break;
            case Drag.TextPathStart:
                DragTextPathStart(ToDocument(releasePosition));
                session.TextEdit?.EndPathStartDrag(keep: true);
                UpdateTextCursor(releasePosition);
                break;
            case Drag.TextResize: guides.Clear(); break;
            case Drag.Guide: FinishGuideDrag(e.GetPosition(this)); break;
        }
        if (pendingMoveDoubleClick && !moved && (finished is Drag.None or Drag.Transform or Drag.LayoutReorder) && session.Tool == Tool.Move)
        {
            if (BeginDoubleMove(e.KeyModifiers.HasFlag(KeyModifiers.Control), shift))
            {
                drag = Drag.None;
                UpdateCursor();
            }
        }
        pendingMoveDoubleClick = false;
        pendingAlignmentAnchor = null;
        if (drag == Drag.None)
        {
            SetModifierHover(e.KeyModifiers.HasFlag(KeyModifiers.Control), alt);
            if (session.Tool == Tool.Move) UpdateMoveHover(releasePosition, e.KeyModifiers);
        }
        if (wasTemporaryMove || finished == Drag.Eyedropper) UpdateCursor();
        InvalidateCanvas();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (recapture)
        {
            recapture = false;
            e.Pointer.Capture(this);
            return;
        }
        // Losing the pointer mid-drag (the window lost focus, a popup opened) must not leave an edit hanging open.
        if (IsDragging || drag == Drag.Pan) { CancelInteraction(); UpdateCursor(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        cursorInside = false;
        if (moveHover != null) { moveHover = null; InvalidateCanvas(); }
        SetModifierHover(control: false, alt: false);
        brushPreparationRequested = false;
        session?.DiscardPreparedBrushDab();
        PointerAt?.Invoke(null);
        if (IsBrushTool) InvalidateBrushCursor();
        else if (layoutGapIndex >= 0)
        {
            layoutGapIndex = -1;
            InvalidateCanvas();
        }
    }

    private void QueueBrushPreparation()
    {
        if (!cursorInside || drag != Drag.None || brushPreparationRequested
            || session?.Tool is not (Tool.Brush or Tool.Eraser or Tool.SpotHealing or Tool.HealingBrush or Tool.CloneStamp)) return;
        brushPreparationRequested = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (cursorInside && drag == Drag.None) session?.PrepareFirstBrushDab();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (session == null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            ZoomTo(zoom * Math.Pow(1.2, e.Delta.Y), e.GetPosition(this), rescale: true);
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) PanBy(new Vector(e.Delta.Y * 60, 0));
        else PanBy(new Vector(e.Delta.X * 60, e.Delta.Y * 60));
        currentDocument = ToDocument(e.GetPosition(this));
        e.Handled = true;
    }

    private void PickColor(bool background)
    {
        if (session == null) return;
        int x = (int)Math.Floor(currentDocument.X), y = (int)Math.Floor(currentDocument.Y);
        if (x < 0 || y < 0 || x >= session.Document.Width || y >= session.Document.Height) return;
        var color = session.SampleColor(x, y);
        if (background) session.Background = color.WithAlpha(255); else session.Foreground = color.WithAlpha(255);
        ToolStateChanged?.Invoke();
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static float Snap(float value) => MathF.Round(value);

    private static SKPoint ConstrainAngle(SKPoint from, SKPoint to, bool constrain)
    {
        if (!constrain) return to;
        float dx = to.X - from.X, dy = to.Y - from.Y;
        var angle = MathF.Round(MathF.Atan2(dy, dx) / (MathF.PI / 4)) * (MathF.PI / 4);
        var length = MathF.Sqrt(dx * dx + dy * dy);
        return new SKPoint(from.X + MathF.Cos(angle) * length, from.Y + MathF.Sin(angle) * length);
    }

    private SKPoint ConstrainAngle(SKPoint to, bool constrain) => ConstrainAngle(pressDocument, to, constrain);

    /// <summary>
    /// Whether Shift squares the marquee being dragged. A Shift held before the press chose the Add or Intersect mode
    /// and keeps meaning that; letting it go and pressing it again mid-drag squares the marquee, as it does in
    /// Photoshop. Any other Shift squares it right away.
    /// </summary>
    private bool ConstrainsMarquee(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift) && (dragMode is not (SelectionMode.Add or SelectionMode.Intersect) || shiftReleased);

    /// <summary>A line's far end: snapped to the targets, or held to 45 degrees from its start when Shift asks for that instead.</summary>
    private SKPoint LineEnd(bool shift) => shift ? ConstrainAngle(snapFrom, currentDocument, true) : snapTo;

    /// <summary>
    /// A marquee or shape corner pulled onto the View > Snap To targets (canvas and layer edges, grid, guides) unless
    /// Ctrl is held, recording the lines to draw. The pointer position itself when snapping is off.
    /// </summary>
    private SKPoint SnapCorner(SKPoint point, bool control)
    {
        guides.Clear();
        if (session == null || !session.View.Snap || control) return point;
        var (snapped, snapX, snapY) = session.SnapPoint(point, (float)(6 / UnitsPerPixel));
        DrawSnapLines(snapX, snapY);
        return snapped;
    }

    private SKPoint SnapLayoutHandle(SKPoint point, SKPoint? direction = null)
    {
        guides.Clear();
        if (session == null) return point;
        var (snapped, x, y) = session.SnapLayoutPoint(point, (float)(6 / UnitsPerPixel), direction);
        DrawSnapLines(x, y);
        return snapped;
    }

    private void DrawSnapLines(float? snapX, float? snapY)
    {
        if (snapX is { } x) guides.Add((new SKPoint(x, -100000), new SKPoint(x, 100000)));
        if (snapY is { } y) guides.Add((new SKPoint(-100000, y), new SKPoint(100000, y)));
    }

    /// <summary>
    /// How far the selection outline has been dragged: Shift keeps it on one axis, and the outline's edges snap to the
    /// targets as a drawn marquee does unless Ctrl is held.
    /// </summary>
    private SKPointI SnappedSelectionOffset(bool shift, bool control)
    {
        guides.Clear();
        float dx = MathF.Round(currentDocument.X - pressDocument.X), dy = MathF.Round(currentDocument.Y - pressDocument.Y);
        bool horizontal = true, vertical = true;
        if (shift) { if (Math.Abs(dx) >= Math.Abs(dy)) { dy = 0; vertical = false; } else { dx = 0; horizontal = false; } }
        if (session != null && session.View.Snap && !control)
        {
            var (snappedX, snappedY, snapX, snapY) = session.SnapSelectionMove(selectionBox, dx, dy, (float)(6 / UnitsPerPixel), horizontal, vertical);
            dx = snappedX; dy = snappedY;
            if (snapX is { } x) guides.Add((new SKPoint(x, -100000), new SKPoint(x, 100000)));
            if (snapY is { } y) guides.Add((new SKPoint(-100000, y), new SKPoint(100000, y)));
        }
        return new SKPointI((int)MathF.Round(dx), (int)MathF.Round(dy));
    }

    /// <summary>The rectangle dragged from the press point, optionally square and/or grown from its center.</summary>
    private SKRect MarqueeRect(bool square, bool fromCenter)
    {
        float dx = Snap(snapTo.X) - Snap(snapFrom.X), dy = Snap(snapTo.Y) - Snap(snapFrom.Y);
        if (square)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = side * (dx < 0 ? -1 : 1);
            dy = side * (dy < 0 ? -1 : 1);
        }
        float x = Snap(snapFrom.X), y = Snap(snapFrom.Y);
        var rect = fromCenter ? new SKRect(x - dx, y - dy, x + dx, y + dy) : new SKRect(x, y, x + dx, y + dy);
        return rect.Standardized;
    }

    // ---- Crop ---------------------------------------------------------------------------------------------------

    private void DragCrop(bool shift, bool alt)
    {
        if (session == null) return;
        var start = cropStart;
        float left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        float px = SnapToCanvas(Snap(currentDocument.X), horizontal: true), py = SnapToCanvas(Snap(currentDocument.Y), horizontal: false);
        if (handle == TransformHandle.Move)
        {
            float dx = Snap(currentDocument.X - pressDocument.X), dy = Snap(currentDocument.Y - pressDocument.Y);
            cropRect = SKRect.Create(start.Left + dx, start.Top + dy, start.Width, start.Height);
            return;
        }
        if (handle is TransformHandle.TopLeft or TransformHandle.Left or TransformHandle.BottomLeft) left = px;
        if (handle is TransformHandle.TopRight or TransformHandle.Right or TransformHandle.BottomRight) right = px;
        if (handle is TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight) top = py;
        if (handle is TransformHandle.BottomLeft or TransformHandle.Bottom or TransformHandle.BottomRight) bottom = py;
        // The bar's ratio always holds; without one, Shift keeps the box's proportions (a fresh box becomes square).
        var ratio = session.CropAspect ?? (shift ? start.Width > 0 && start.Height > 0 ? start.Width / (double)start.Height : 1 : null);
        if (ratio is { } aspect)
        {
            var movesLeft = handle is TransformHandle.TopLeft or TransformHandle.Left or TransformHandle.BottomLeft;
            var movesRight = handle is TransformHandle.TopRight or TransformHandle.Right or TransformHandle.BottomRight;
            var movesTop = handle is TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight;
            var movesBottom = handle is TransformHandle.BottomLeft or TransformHandle.Bottom or TransformHandle.BottomRight;
            // The edge opposite the handle stays put; the pointer's edge sets the size along its own axis.
            float anchorX = movesLeft ? start.Right : start.Left, anchorY = movesTop ? start.Bottom : start.Top;
            float draggedX = movesLeft ? left : right, draggedY = movesTop ? top : bottom;
            float w = Math.Abs(draggedX - anchorX), h = Math.Abs(draggedY - anchorY);
            if ((movesLeft || movesRight) && !movesTop && !movesBottom)
            {
                h = (float)(w / aspect);
                left = draggedX >= anchorX ? anchorX : anchorX - w; right = left + w;
                top = start.MidY - h / 2; bottom = top + h;
            }
            else if ((movesTop || movesBottom) && !movesLeft && !movesRight)
            {
                w = (float)(h * aspect);
                top = draggedY >= anchorY ? anchorY : anchorY - h; bottom = top + h;
                left = start.MidX - w / 2; right = left + w;
            }
            else
            {
                if (w / aspect > h) h = (float)(w / aspect); else w = (float)(h * aspect);
                left = draggedX >= anchorX ? anchorX : anchorX - w; right = left + w;
                top = draggedY >= anchorY ? anchorY : anchorY - h; bottom = top + h;
            }
        }
        if (alt)
        {
            // Symmetric cropping: the opposite edge mirrors the dragged one.
            float cx = start.Width > 0 ? start.MidX : start.Left, cy = start.Height > 0 ? start.MidY : start.Top;
            if (left != start.Left) right = 2 * cx - left; else if (right != start.Right) left = 2 * cx - right;
            if (top != start.Top) bottom = 2 * cy - top; else if (bottom != start.Bottom) top = 2 * cy - bottom;
        }
        cropRect = new SKRect(left, top, right, bottom).Standardized;
    }

    /// <summary>Crop edges snap to the View > Snap To targets (without centers); Ctrl bypasses snapping.</summary>
    private float SnapToCanvas(float value, bool horizontal)
    {
        if (session == null || !session.View.Snap || dragModifiers.HasFlag(KeyModifiers.Control)) return value;
        return session.SnapCropEdge(value, horizontal, (float)(8 / UnitsPerPixel));
    }

    public void ApplyCrop()
    {
        if (session == null || cropRect is not { } crop) return;
        var rect = new SKRectI((int)Math.Round(crop.Left), (int)Math.Round(crop.Top), (int)Math.Round(crop.Right), (int)Math.Round(crop.Bottom));
        cropRect = null;
        if (rect.Width >= 1 && rect.Height >= 1) session.Crop(rect);
        Fit();
        ToolStateChanged?.Invoke();
    }

    public void CancelCrop()
    {
        cropRect = null;
        InvalidateCanvas();
        ToolStateChanged?.Invoke();
    }

    // ---- Move / transform ---------------------------------------------------------------------------------------

    private static SKPoint[] Corners(SKRect r) => [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];

    /// <summary>The transform frame for the current selection, as document-space corners, or null.</summary>
    private SKPoint[]? CurrentFrame()
    {
        if (session == null) return null;
        if (session.Transform is { } edit) return edit.Corners();
        if (SelectedLayoutGroup() != null) return null;
        if (session.IsEditingChannel && session.Selection is { } channelSelection) return Corners(SelectionMask.Bounds(channelSelection));
        if (session.IndependentMaskTarget is { Mask: { } mask } maskLayer)
            return maskLayer.MaskTransform?.FrameCorners() ?? MaskCorners(maskLayer, mask);
        var targets = session.TransformTargets();
        if (targets.Count == 0) return null;
        if (targets.Count == 1)
        {
            // A distorted layer's handles sit on its corners, wherever they were dragged to.
            var target = targets[0];
            return target.ControlTransform.FrameCorners();
        }
        var bounds = SKRect.Empty;
        foreach (var layer in targets) bounds = bounds.IsEmpty ? layer.ControlBounds : SKRect.Union(bounds, layer.ControlBounds);
        return Corners(bounds);
    }

    private static SKPoint[] MaskCorners(Layer layer, SKBitmap mask) =>
        [layer.MaskMatrix.MapPoint(0, 0), layer.MaskMatrix.MapPoint(mask.Width, 0), layer.MaskMatrix.MapPoint(mask.Width, mask.Height), layer.MaskMatrix.MapPoint(0, mask.Height)];

    private static SKPoint RotationGrip(SKPoint a, SKPoint b, SKPoint opposite)
    {
        var mid = new SKPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        float dx = b.Y - a.Y, dy = a.X - b.X;
        if (dx * (opposite.X - mid.X) + dy * (opposite.Y - mid.Y) > 0) { dx = -dx; dy = -dy; }
        float scale = 23 / Math.Max(.01f, MathF.Sqrt(dx * dx + dy * dy));
        return new(mid.X + dx * scale, mid.Y + dy * scale);
    }

    /// <summary>Which handle of a frame (document-space corners) is under a screen point.</summary>
    private TransformHandle HitFrame(SKPoint[] corners, Point screen, bool allowRotate)
    {
        var points = corners.Select(ToScreen).ToArray();
        Point Mid(int a, int b) => new((points[a].X + points[b].X) / 2, (points[a].Y + points[b].Y) / 2);
        (Point P, TransformHandle H)[] handles =
        [
            (points[0], TransformHandle.TopLeft), (points[1], TransformHandle.TopRight), (points[2], TransformHandle.BottomRight), (points[3], TransformHandle.BottomLeft),
            (Mid(0, 1), TransformHandle.Top), (Mid(1, 2), TransformHandle.Right), (Mid(2, 3), TransformHandle.Bottom), (Mid(3, 0), TransformHandle.Left)
        ];
        foreach (var (p, h) in handles) if (Distance(p, screen) <= 7) return h;
        if (allowRotate)
        {
            SKPoint P(int i) => new((float)points[i].X, (float)points[i].Y);
            var grip = RotationGrip(P(0), P(1), P(2));
            if (Distance(new Point(grip.X, grip.Y), screen) <= 9) return TransformHandle.Rotate;
        }
        var inside = Contains(points, screen);
        if (inside) return TransformHandle.Move;
        if (allowRotate) foreach (var p in points) if (Distance(p, screen) <= 26) return TransformHandle.Rotate;
        return TransformHandle.None;
    }

    private static bool Contains(Point[] polygon, Point p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            if (polygon[i].Y > p.Y != polygon[j].Y > p.Y && p.X < (polygon[j].X - polygon[i].X) * (p.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        return inside;
    }

    private void UpdateMoveCursor(Point position)
    {
        var type = StandardCursorType.Arrow;
        if (RulerAt(position) is { } axis) type = axis == GuideAxis.Vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth;
        else
        {
            var hit = ShowTransformControls && CurrentFrame() is { } frame
                ? HitFrame(frame, position, allowRotate: true) : TransformHandle.None;
            if (hit is not (TransformHandle.None or TransformHandle.Move))
                type = hit switch
                {
                    TransformHandle.TopLeft or TransformHandle.BottomRight => StandardCursorType.TopLeftCorner,
                    TransformHandle.TopRight or TransformHandle.BottomLeft => StandardCursorType.TopRightCorner,
                    TransformHandle.Top or TransformHandle.Bottom => StandardCursorType.SizeNorthSouth,
                    TransformHandle.Left or TransformHandle.Right => StandardCursorType.SizeWestEast,
                    TransformHandle.Rotate => StandardCursorType.Cross,
                    _ => StandardCursorType.Arrow
                };
            else if (HitGuide(position) is { } guide)
                type = guide.Axis == GuideAxis.Vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth;
            else if (hit == TransformHandle.Move) type = StandardCursorType.SizeAll;
        }
        SetCanvasStandardCursor(type);
    }

    /// <summary>Whether the one layer being transformed already has a corner pulled out of place.</summary>
    private bool IsDistorted() => session?.IndependentMaskTarget is { MaskTransform.Distort: not null }
        || session?.TransformTargets() is [{ Pixels: not null, Transform.Distort: not null }];

    private Layer? MoveGroupScope => moveGroupScope is { } id ? session?.Document.Find(id) : null;

    /// <summary>Promotes the top visible hit to the immediate child of the current group scope.</summary>
    private Layer? MoveLayerAt(SKPoint point, bool pickChild)
    {
        if (session == null) return null;
        var scope = pickChild ? null : MoveGroupScope;
        if (scope == null) return PickLayer(session.Document.Layers, point, (float)zoom, pickChild, [], 0);
        var parents = new List<Layer>();
        for (var group = scope; group != null; group = session.Document.ParentOf(group.Id))
        {
            if (!group.Visible) return null;
            parents.Add(group);
        }
        parents.Reverse();
        return PickLayer(scope.Children, point, (float)zoom, false, parents, parents.Count);
    }

    private static Layer? PickLayer(List<Layer> layers, SKPoint point, float scale, bool pickChild, List<Layer> parents, int scopeDepth)
    {
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (!layer.Visible) continue;
            // Live text is picked by its editable box, including spaces and thin glyphs. Keep stack order:
            // an opaque layer above the text still wins, and layer/group masks still constrain the hit.
            if (layer.Pixels != null && layer.Bounds.Contains(point) && PickCoverage(layer, point, scale, parents) > 12)
                return pickChild || parents.Count == scopeDepth ? layer : parents[scopeDepth];
            if (!pickChild && layer.AutoLayout != null && layer.Opacity > 0 &&
                EditorSession.LayoutBounds(layer).Contains(point))
                return parents.Count == scopeDepth ? layer : parents[scopeDepth];
            if (layer.Children.Count == 0) continue;
            parents.Add(layer);
            var child = PickLayer(layer.Children, point, scale, pickChild, parents, scopeDepth);
            parents.RemoveAt(parents.Count - 1);
            if (child != null) return child;
        }
        return null;
    }

    private bool BeginDoubleMove(bool control, bool shift)
    {
        if (session == null || shift || session.Document.SelectedLayerIds.Count > 1) return false;
        var hit = MoveLayerAt(pressDocument, control);
        if (!control && hit?.IsGroup == true)
        {
            var direct = MoveLayerAt(pressDocument, true);
            if (direct != null && direct.Id == session.Document.ActiveLayerId && direct.Text != null)
                return BeginLiveTextEdit(direct, shift);
            moveGroupScope = hit.Id;
            session.SelectLayer(MoveLayerAt(pressDocument, false)?.Id ?? hit.Id);
            moveHover = null;
            return true;
        }
        return hit?.Text != null && BeginLiveTextEdit(hit, shift);
    }

    private Layer? MoveTarget(SKPoint point, TransformHandle handle, bool direct, bool extend)
    {
        if (session == null) return null;
        if (!direct && !extend && session.Document.SelectedLayerIds.Count == 1 &&
            session.ActiveLayer is { IsGroup: true } group && group != MoveGroupScope &&
            (handle == TransformHandle.Move || group.AutoLayout != null && EditorSession.LayoutBounds(group).Contains(point)))
            return group;
        return MoveLayerAt(point, direct);
    }

    private void UpdateMoveHover(Point position, KeyModifiers modifiers)
    {
        if (session == null) return;
        var frame = ShowTransformControls && CurrentFrame() is { } current
            ? HitFrame(current, position, allowRotate: true) : TransformHandle.None;
        bool direct = modifiers.HasFlag(KeyModifiers.Control), extend = modifiers.HasFlag(KeyModifiers.Shift);
        var target = !spaceDown && !OverRuler(position) && frame is TransformHandle.None or TransformHandle.Move &&
            session.IndependentMaskTarget == null && !session.IsEditingChannel &&
            !(frame == TransformHandle.Move && transformSelection != null && !direct && !extend) &&
            !(session.CanMovePixels && InsideSelection(currentDocument))
            ? (AutoSelect || direct ? MoveTarget(ToDocument(position), frame, direct, extend) : session.ActiveLayer) : null;
        var id = target?.Id;
        if (id == moveHover) return;
        moveHover = id;
        InvalidateCanvas();
    }

    private static double PickCoverage(Layer layer, SKPoint point, float scale, List<Layer> parents)
    {
        var alpha = layer.HitCoverage(point, scale) * 255 * layer.Opacity;
        if (alpha == 0) return 0;
        for (var i = parents.Count - 1; i >= 0; i--)
        {
            var parent = parents[i];
            alpha *= parent.Opacity;
            if (parent.RenderMask() is { } parentMask)
            {
                if (!parent.MaskMatrix.TryInvert(out var maskInverse)) return 0;
                var maskPoint = maskInverse.MapPoint(point);
                int px = (int)Math.Floor(maskPoint.X), py = (int)Math.Floor(maskPoint.Y);
                alpha *= (px >= 0 && py >= 0 && px < parentMask.Width && py < parentMask.Height ? parentMask.GetPixel(px, py).Alpha : parent.MaskDefault) / 255.0;
            }
        }
        return alpha;
    }

    /// <summary>
    /// The window reports key presses and releases so the cursor can change while the pointer is stationary.
    /// A modifier key's own events carry the modifier state from before the key
    /// changed, so the key itself decides.
    /// </summary>
    public void ModifierKeyChanged(Key key, KeyModifiers modifiers, bool down)
    {
        if (drag != Drag.None)
        {
            // Update the resize and its guides even when the pointer is stationary.
            var flag = key switch
            {
                Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
                Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
                Key.LeftCtrl or Key.RightCtrl when IsResizeDrag => KeyModifiers.Control,
                _ => KeyModifiers.None
            };
            if (flag == KeyModifiers.None) return;
            dragModifiers = down ? dragModifiers | flag : dragModifiers & ~flag;
            if (drag == Drag.Marquee && flag == KeyModifiers.Shift && !down) shiftReleased = true;
            if (UpdatesTransformModifiers && Distance(pressScreen, cursorScreen) > 2)
                DragTransform(dragModifiers.HasFlag(KeyModifiers.Shift), dragModifiers.HasFlag(KeyModifiers.Alt),
                    dragModifiers.HasFlag(KeyModifiers.Control));
            if (drag == Drag.PenNode && flag == KeyModifiers.Shift && Distance(pressScreen, cursorScreen) > 2)
                PenMove(dragModifiers.HasFlag(KeyModifiers.Alt), dragModifiers.HasFlag(KeyModifiers.Shift));
            InvalidateCanvas();
            return;
        }
        var control = key is Key.LeftCtrl or Key.RightCtrl ? down : modifiers.HasFlag(KeyModifiers.Control);
        var alt = key is Key.LeftAlt or Key.RightAlt ? down : modifiers.HasFlag(KeyModifiers.Alt);
        SetModifierHover(control, alt);
    }

    private void SetModifierHover(bool control, bool alt)
    {
        var move = control && cursorInside && session != null && session.Tool != Tool.Move;
        var sample = alt && cursorInside && session?.Tool == Tool.Brush;
        if (move == controlHover && sample == altHover) return;
        controlHover = move;
        altHover = sample;
        UpdateCursor();
    }

    /// <summary>
    /// Ctrl-drag with any other tool moves the current layers without switching tools. Like a plain Move-tool drag, a
    /// press on another layer's pixels that misses the current frame moves that layer instead; handles are not offered.
    /// </summary>
    private void BeginTemporaryMove()
    {
        if (session == null) return;
        handle = TransformHandle.Move;
        distortCorner = -1;
        distortEdge = false;
        var frame = CurrentFrame();
        var hit = MoveLayerAt(pressDocument, pickChild: false);
        if (hit != null && !session.Document.SelectedLayerIds.Contains(hit.Id) && (frame == null || HitFrame(frame, pressScreen, allowRotate: false) == TransformHandle.None))
            session.SelectLayer(hit.Id, extend: false);
        if (session.BeginTransform("Move", duplicate: dragModifiers.HasFlag(KeyModifiers.Alt)) == null)
        {
            Problem?.Invoke("Select a layer with pixels to move.");
            return;
        }
        temporaryMove = true;
        drag = Drag.Transform;
        UpdateCursor();
    }

    private void BeginMove(bool control, int clicks, TransformHandle moveHandle)
    {
        if (session == null) return;
        pendingAlignmentAnchor = null;
        handle = moveHandle;
        distortCorner = -1;
        distortEdge = false;
        var onHandle = handle is not (TransformHandle.None or TransformHandle.Move);
        var outsideCanvas = !session.Document.Bounds.Contains((int)Math.Floor(pressDocument.X), (int)Math.Floor(pressDocument.Y));
        var keepFrame = handle == TransformHandle.Move && (transformSelection != null || outsideCanvas)
            && !control && !dragModifiers.HasFlag(KeyModifiers.Shift);
        if (!onHandle && !keepFrame && session.IndependentMaskTarget == null && !session.IsEditingChannel)
        {
            bool extend = dragModifiers.HasFlag(KeyModifiers.Shift);
            var hit = MoveTarget(pressDocument, handle, control, extend);
            if (hit == null && (outsideCanvas || AutoSelect || control || clicks >= 2))
            {
                session.ClearLayerSelection();
                return;
            }
            if (hit != null && (AutoSelect || control || clicks >= 2))
            {
                if (!session.Document.SelectedLayerIds.Contains(hit.Id) || extend)
                    session.SelectLayer(hit.Id, extend: extend);
                else if (!control && clicks == 1 && !dragModifiers.HasFlag(KeyModifiers.Alt) && session.AlignableObjectCount > 1)
                    pendingAlignmentAnchor = hit.Id;
                else if (hit.Id == session.Document.ActiveLayerId) session.RevealActiveLayer();
            }
            if (control && dragModifiers == KeyModifiers.Control && BeginLayoutReorder()) return;
            handle = TransformHandle.Move;
        }
        else if (!onHandle) handle = TransformHandle.Move;
        else if ((control || IsDistorted()) && handle is TransformHandle.TopLeft or TransformHandle.TopRight or TransformHandle.BottomRight or TransformHandle.BottomLeft)
        {
            // Ctrl-dragging a corner distorts, as in Photoshop; once a layer is distorted its corners keep distorting.
            distortCorner = handle switch { TransformHandle.TopLeft => 0, TransformHandle.TopRight => 1, TransformHandle.BottomRight => 2, _ => 3 };
        }
        else if (control && handle is TransformHandle.Top or TransformHandle.Right or TransformHandle.Bottom or TransformHandle.Left)
            distortEdge = true;
        if (session.BeginTransform(handle == TransformHandle.Move ? "Move" : handle == TransformHandle.Rotate ? "Rotate"
            : distortCorner >= 0 || distortEdge ? "Distort" : "Scale", duplicate: handle == TransformHandle.Move && dragModifiers.HasFlag(KeyModifiers.Alt)) == null)
        {
            Problem?.Invoke("Select a layer with pixels to move.");
            return;
        }
        drag = Drag.Transform;
    }

    private void DragTransform(bool shift, bool alt, bool control)
    {
        if (session?.Transform is not { } edit) return;
        guides.Clear();
        switch (handle)
        {
            case TransformHandle.Move:
                // Selecting a group can reflow the toolbar, moving the canvas under a
                // stationary pointer. Measure a translation in stable window coordinates.
                var root = TopLevel.GetTopLevel(this);
                var screen = root != null ? this.TranslatePoint(cursorScreen, root) ?? cursorScreen : cursorScreen;
                float dx = (float)((screen.X - pressTopLevel.X) / UnitsPerPixel);
                float dy = (float)((screen.Y - pressTopLevel.Y) / UnitsPerPixel);
                if (shift) { if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0; }
                dx = MathF.Round(dx); dy = MathF.Round(dy);
                // Ctrl is what started a temporary move, so it cannot also mean "no snapping" there.
                if (!control || temporaryMove) SnapMove(edit, ref dx, ref dy);
                edit.MoveBy(dx, dy);
                break;
            case TransformHandle.Rotate:
                edit.RotateTo(pressDocument, currentDocument, shift);
                break;
            default:
                if (distortCorner >= 0)
                {
                    var (x, y) = edit.DistortCorner(distortCorner, currentDocument, perspective: shift && alt,
                        snapTolerance: (float)(6 / UnitsPerPixel), keepConvex: true);
                    DrawSnapLines(x, y);
                }
                else if (distortEdge)
                {
                    var (x, y) = edit.DistortEdge(handle, currentDocument - pressDocument, skew: shift, fromCenter: alt,
                        snapTolerance: (float)(6 / UnitsPerPixel), keepConvex: true);
                    DrawSnapLines(x, y);
                }
                else
                {
                    var (x, y) = edit.Resize(handle, currentDocument, free: shift, fromCenter: alt,
                        snapTolerance: control ? 0 : (float)(6 / UnitsPerPixel));
                    DrawSnapLines(x, y);
                }
                break;
        }
        TransformPreviewChanged?.Invoke();
    }

    /// <summary>Pulls a move onto the View > Snap To targets (canvas, layers, grid, guides), recording the lines to draw.</summary>
    private void SnapMove(TransformEdit edit, ref float dx, ref float dy)
    {
        if (session == null || !session.View.Snap) return;
        var threshold = (float)(6 / UnitsPerPixel);
        var rotate = SKMatrix.CreateRotationDegrees((float)edit.StartRotation, edit.StartFrame.MidX, edit.StartFrame.MidY);
        var box = rotate.MapRect(edit.StartFrame);
        var (snappedX, snappedY, snapX, snapY) = session.SnapMove(box, edit.MovingLayerIds, dx, dy, threshold);
        dx = snappedX; dy = snappedY;
        DrawSnapLines(snapX, snapY);
    }

    // ---- Keyboard -----------------------------------------------------------------------------------------------

    private void FinishPolygon()
    {
        if (session == null) return;
        if (polygon.Count > 2) session.SelectPolygon(polygon.ToList(), dragMode);
        polygon.Clear();
        drag = Drag.None;
    }

    /// <summary>Tool keys that depend on what the canvas is doing. Returns true when the key was used.</summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && drag == Drag.None
            && session?.Tool == Tool.PromptRegion && ActiveRegion() is { } region)
        {
            session.DeletePromptRegion(region.Number);
            return true;
        }
        if (session == null) return false;
        if (session.WarpEdit != null)
        {
            if (e.Key == Key.Escape) { FinishWarp(keep: false); return true; }
            if (e.Key == Key.Enter) { if (!IsDragging) FinishWarp(keep: true); return true; }
            if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { UndoWarpPoint(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); return true; }
            if (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control) { UndoWarpPoint(redo: true); return true; }
            if (e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && e.Key is Key.Left or Key.Right or Key.Up or Key.Down) { NudgeWarpPoint(e); return true; }
        }
        if (session.TextEdit is { } editor) return HandleTextKey(editor, e);
        if (session.Tool == Tool.Pen)
        {
            if (e.Key == Key.Escape) { CancelInteraction(); penDraft.Clear(); ClearPenSelection(); InvalidateCanvas(); return true; }
            if (e.Key is Key.LeftAlt or Key.RightAlt && drag == Drag.None && penDraft.Count > 0 && cursorInside)
            {
                var last = penDraft[^1];
                if (SKPoint.Distance(ToDocument(cursorScreen), new SKPoint((float)last.X,(float)last.Y))*UnitsPerPixel < 9)
                {
                    penDraft[^1] = last with { OutX = last.X, OutY = last.Y };
                    InvalidateCanvas(); return true;
                }
            }
            if (e.Key == Key.Enter) { FinishPen(); return true; }
            if (drag == Drag.None && e.Key is Key.Back or Key.Delete && penDraft.Count > 0) { DeleteDraftPoint(); return true; }
        }
        if (session.Tool is Tool.Pen or Tool.Shape && drag == Drag.None && e.Key is Key.Back or Key.Delete && DeletePenNode()) return true;
        if (session.ColorRange != null && drag == Drag.None)
        {
            // The panel's OK and Cancel, reached from the canvas too.
            if (e.Key == Key.Escape) { session.CancelColorRange(); return true; }
            if (e.Key == Key.Enter) { session.CommitColorRange(); return true; }
        }
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (HasPendingGradient && drag == Drag.None)
        {
            // Undo while a gradient is still adjustable takes the gradient back, as it would once applied.
            if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control) { SettleGradient(keep: false); return true; }
            if (e.Key == Key.Escape) { SettleGradient(keep: false); return true; }
            if (e.Key == Key.Enter) { SettleGradient(keep: true); return true; }
        }
        // Mid-drag only Escape (cancel) and Space (pan) mean anything; everything else waits for the drag to end.
        if (IsDragging && e.Key is not (Key.Escape or Key.Space)) return true;
        if (e.Key == Key.Escape && drag == Drag.Transform) { CancelInteraction(); return true; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        switch (e.Key)
        {
            case Key.Space:
                if (!spaceDown) { spaceDown = true; UpdateCursor(); }
                return true;
            case Key.Escape:
                if (drag != Drag.None || polygon.Count > 0) { CancelInteraction(); return true; }
                if (cropRect != null) { CancelCrop(); return true; }
                if (session.Tool == Tool.Move && MoveGroupScope is { } scope)
                {
                    moveGroupScope = session.Document.ParentOf(scope.Id)?.Id;
                    moveHover = null;
                    session.SelectLayer(scope.Id);
                    return true;
                }
                return false;
            case Key.Enter:
                if (polygon.Count > 0) { FinishPolygon(); InvalidateCanvas(); return true; }
                if (cropRect != null) { ApplyCrop(); return true; }
                return false;
            case Key.Back or Key.Delete when polygon.Count > 0:
                polygon.RemoveAt(polygon.Count - 1);
                if (polygon.Count == 0) drag = Drag.None;
                InvalidateCanvas();
                return true;
            case Key.OemOpenBrackets or Key.OemCloseBrackets when IsBrushTool:
                var grow = e.Key == Key.OemCloseBrackets;
                if (session.Tool is Tool.SelectionBrush or Tool.RemoveObject) session.SelectionBrushSize = NextBrushSize(session.SelectionBrushSize, grow);
                else if (shift) session.Brush = session.Brush with { Hardness = Math.Clamp(session.Brush.Hardness + (grow ? 0.25 : -0.25), 0, 1) };
                else session.Brush = session.Brush with { Size = Math.Min(Composa.Painting.BrushSettings.MaxEditorDiameter, NextBrushSize(session.Brush.Size, grow)) };
                ToolStateChanged?.Invoke();
                InvalidateBrushCursor();
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var step = shift ? 10 : 1;
                int dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                if (session.Tool == Tool.Move) session.Nudge(dx, dy);
                else if (session.Tool is Tool.Marquee or Tool.Lasso or Tool.Wand && session.Selection != null) session.MoveSelection(dx, dy);
                else return false;
                return true;
        }
        if (e.Key is >= Key.D0 and <= Key.D9 && !shift)
        {
            var value = e.Key == Key.D0 ? 1.0 : (e.Key - Key.D0) / 10.0;
            if (IsBrushTool && session.Tool is not (Tool.SelectionBrush or Tool.RemoveObject)) session.Brush = session.Brush with { Opacity = value };
            else if (session.Tool == Tool.Gradient) session.GradientOpacity = value;
            else if (session.Tool == Tool.Move && session.ActiveLayer is { } layer)
            {
                session.Apply("Opacity", () => session.SetOpacity(layer, value));
                session.NotifyLayersChanged();
            }
            else return false;
            ToolStateChanged?.Invoke();
            return true;
        }
        return false;
    }

    public void HandleKeyUp(KeyEventArgs e)
    {
        if (e.Key != Key.Space || !spaceDown) return;
        spaceDown = false;
        UpdateCursor();
    }

    private static double NextBrushSize(double size, bool grow)
    {
        var step = size < 10 ? 1 : size < 50 ? 5 : size < 100 ? 10 : size < 300 ? 25 : 50;
        return Math.Clamp(grow ? size + step : size - (size <= 10 ? 1 : size <= 50 ? 5 : size <= 100 ? 10 : size <= 300 ? 25 : 50), 1, 2500);
    }
}
