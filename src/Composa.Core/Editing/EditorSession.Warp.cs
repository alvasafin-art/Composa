using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Editing;

/// <summary>A retained-source mesh edit. Pointer updates only replace geometry, never the source pixels or placement.</summary>
public sealed class WarpEdit
{
    private readonly EditorSession session;
    internal WarpMesh? Original { get; }
    public Layer Layer { get; }
    public WarpMesh Mesh => Layer.Warp!;
    internal WarpEdit(EditorSession session, Layer layer, WarpMesh mesh)
    { this.session = session; Layer = layer; Original = layer.Warp; layer.Warp = mesh; }

    public void MovePoint(int index, SKPoint documentPoint)
    {
        if (session.WarpEdit != this) return;
        var previous = Mesh.Points[index];
        var current = Layer.Matrix.MapPoint((float)(previous.X * Layer.Pixels!.Width), (float)(previous.Y * Layer.Pixels.Height));
        if (Math.Abs(current.X - documentPoint.X) + Math.Abs(current.Y - documentPoint.Y) < 1e-5) return;
        if (!Layer.Matrix.TryInvert(out var inverse)) throw new InvalidOperationException("The layer transform is singular.");
        var point = inverse.MapPoint(documentPoint);
        SetMesh(Mesh.WithPoint(index, new((double)point.X / Layer.Pixels!.Width, (double)point.Y / Layer.Pixels.Height)));
    }

    public void Reset() => SetMesh(WarpMesh.Create(Mesh.Columns, Mesh.Rows, Mesh.Domain));

    public void ChangeGrid(int columns, int rows)
    {
        if (!Mesh.IsIdentity) throw new InvalidOperationException("Reset the deformation before changing its grid.");
        SetMesh(WarpMesh.Create(columns, rows, Mesh.Domain));
    }

    public void SetMesh(WarpMesh mesh)
    {
        if (session.WarpEdit != this || mesh.Equals(Layer.Warp)) return;
        EditorSession.ValidateWarp(Layer, mesh);
        var before = session.AffectedArea(Layer); Layer.Warp = mesh;
        session.Invalidate(Geometry.Union(before, session.AffectedArea(Layer)));
    }
}

public sealed partial class EditorSession
{
    public WarpEdit? WarpEdit { get; private set; }
    public bool CanWarp => !IsEditingChannel && !IsEditingMask && document.SelectedLayerIds.Count == 1 && ActiveLayer is { Pixels: not null } layer && !PositionLocked(layer) && !PixelsLocked(layer);

    public WarpEdit BeginWarp(int columns = 3, int rows = 3)
    {
        FinishInteraction();
        if (!CanWarp) throw new InvalidOperationException("Select an unlocked image layer to warp.");
        var layer = ActiveLayer!;
        var mesh = WarpGridFor(layer, columns, rows);
        ValidateWarp(layer, mesh); Begin("Warp");
        WarpEdit = new(this, layer, mesh); InvalidateAll(); return WarpEdit;
    }

    public static WarpMesh WarpGridFor(Layer layer, int columns = 3, int rows = 3)
    {
        if (layer.Warp != null) return layer.Warp;
        var pixels = layer.Pixels ?? throw new InvalidOperationException("A Warp surface requires image pixels.");
        var bounds = layer.ControlSourceBounds;
        return WarpMesh.Create(columns, rows, new((double)bounds.Left / pixels.Width, (double)bounds.Top / pixels.Height,
            (double)bounds.Width / pixels.Width, (double)bounds.Height / pixels.Height));
    }

    public void CommitWarp()
    {
        if (WarpEdit is not { } edit) return;
        WarpEdit = null;
        var mesh = edit.Mesh;
        if (mesh.IsIdentity) edit.Layer.Warp = null;
        if (Equals(edit.Layer.Warp, edit.Original))
        {
            // Geometry was the only preview mutation. Keep the live document/layer references when another
            // command settles an unchanged Warp; that command may already hold its target layer.
            edit.Layer.Warp = edit.Original;
            if (transactionDepth == 0) pendingBefore = null;
            InvalidateAll(); LayersChanged?.Invoke();
        }
        else
        {
            try
            {
                var result = edit.Layer.Warp;
                if (result != null)
                {
                    edit.Layer.Warp = edit.Original; EmbedWarpCoverage(edit.Layer); edit.Layer.Warp = result;
                }
                Commit(); InvalidateAll(); LayersChanged?.Invoke();
            }
            catch { Cancel(); throw; }
        }
    }

    public void CancelWarp()
    {
        if (WarpEdit is not { } edit) return;
        WarpEdit = null;
        if (transactionDepth > 0) { edit.Layer.Warp = edit.Original; InvalidateAll(); LayersChanged?.Invoke(); }
        else Cancel();
    }

    public void SetWarp(Layer layer, WarpMesh? mesh)
    {
        FinishInteraction();
        if (document.Find(layer.Id) != layer || layer.Pixels == null) throw new InvalidOperationException("The image layer is no longer in this document.");
        if (PixelsLocked(layer) || PositionLocked(layer)) throw new InvalidOperationException("Unlock the layer before warping it.");
        ValidateWarp(layer, mesh);
        if (mesh?.IsIdentity == true) mesh = null;
        if (Equals(mesh, layer.Warp)) return;
        Apply("Warp", () => { if (mesh != null) EmbedWarpCoverage(layer); layer.Warp = mesh; }); InvalidateAll(); LayersChanged?.Invoke();
    }

    internal static void ValidateWarp(Layer layer, WarpMesh? mesh)
    {
        if (mesh == null) return;
        var pixels = layer.Pixels ?? throw new InvalidOperationException("A Warp surface requires image pixels.");
        var bounds = Geometry.RoundOut(mesh.Bounds(pixels.Width, pixels.Height));
        if (!DocumentLimits.FitsSurface(bounds.Width, bounds.Height, pixels.BytesPerPixel))
            throw new InvalidOperationException($"The deformed surface exceeds the {DocumentLimits.MaxSide}-pixel side or supported surface memory limit.");
    }

    /// <summary>Keep the undeformed pixels, live content, filters and source masks editable in a smart object.
    /// New outer masks can be painted in document space instead of pretending Warp has an affine brush matrix.</summary>
    private void EmbedWarpCoverage(Layer layer)
    {
        if (layer.IsSmartObject && layer.FilterSource == null && !(layer.Mask != null && layer.MaskLinked && layer.MaskFollowsWarp) && !(layer.VectorMask != null && layer.VectorMaskFollowsWarp)) return;
        var pixels = layer.Pixels!;
        var content = new Document(pixels.Width, pixels.Height) { Depth = document.WorkingDepth, Profile = document.Profile, Resolution = document.Resolution };
        var inner = layer.Clone(newIds: true); inner.Transform = LayerTransform.Identity(pixels.Width, pixels.Height);
        if (!layer.MaskLinked || !layer.MaskFollowsWarp) inner.Mask = null;
        if (!layer.VectorMaskFollowsWarp) inner.VectorMask = null;
        inner.Warp = null; inner.Effects = null; inner.Visible = true; inner.Opacity = inner.FillOpacity = 1; inner.Blend = BlendMode.Normal; inner.BlendIf = new(); inner.Clipped = false;
        content.Layers.Add(inner); content.SetActive(inner.Id);
        var source = SmartObjectSource.Create(content);
        void Assign(Layer target)
        {
            target.SmartObject = source; target.Pixels = source.Preview; target.Shape = null; target.Text = null;
            target.FilterSource = null; target.SmartFilters = []; target.SmartFilterMask = null; target.FilterPaddingX = target.FilterPaddingY = 0;
            if (layer.VectorMaskFollowsWarp) target.VectorMask = null;
            if (layer.MaskLinked && layer.MaskFollowsWarp) target.Mask = null;
        }
        try
        {
            var candidate = document.Clone(); Assign(candidate.Find(layer.Id)!); EnsureSmartBudget(candidate);
        }
        catch { source.Preview.Dispose(); throw; }
        Assign(layer);
    }
}
