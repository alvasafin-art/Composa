using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public sealed class AutoLayoutTests
{
    [Fact]
    public void Infers_column_preserves_geometry_and_undoes_a_gap_gesture_as_one_step()
    {
        var a = Raster("Photo", 160, 100, 24, 20);
        var b = Raster("Heading", 140, 25, 24, 132);
        var c = Raster("Caption", 100, 15, 24, 169);
        var s = Session(a, b, c);
        Select(s, a, b, c);
        var original = new[] { a, b, c }.Select(layer => layer.Transform).ToArray();
        var proposal = Assert.IsType<AutoLayoutProposal>(s.ProposeAutoLayout(out var problem));
        Assert.Null(problem);
        Assert.Equal(LayoutDirection.Column, proposal.Settings.Direction);
        Assert.Equal(12, proposal.Settings.Gap);
        Assert.False(proposal.ChangesPositions);
        Assert.True(s.CreateAutoLayout(proposal));
        var group = Assert.IsType<Layer>(s.ActiveLayer);
        Assert.Equal(new[] { c, b, a }, group.Children);
        Assert.Equal(original, new[] { a, b, c }.Select(layer => layer.Transform));

        var state = s.History.CurrentId;
        Assert.True(s.BeginLayoutGap(group));
        s.PreviewLayoutGap(group, 16);
        s.PreviewLayoutGap(group, 24);
        s.FinishLayoutGap(group, keep: true);
        Assert.Equal(144, b.Transform.Y);
        s.Undo();
        Assert.Equal(state, s.History.CurrentId);
        group = s.Document.Find(group.Id)!;
        Assert.Equal(12, group.AutoLayout!.Gap);
        s.Redo();
        group = s.Document.Find(group.Id)!;
        Assert.Equal(24, group.AutoLayout!.Gap);
        Assert.True(s.ReorderAutoLayout(group, c.Id, 0));
        Assert.Equal(c.Id, s.LayoutElements(group)[0].Layer.Id);
        var placed = group.Children.Select(layer => layer.Transform).ToArray();
        s.DisableAutoLayout(group);
        Assert.Null(group.AutoLayout);
        Assert.Equal(placed, group.Children.Select(layer => layer.Transform));
    }

    [Fact]
    public void Nested_price_moves_with_icon_and_title_wrap_and_remains_editable_after_save()
    {
        var photo = Raster("Photo", 210, 110, 30, 20);
        var icon = Raster("Icon", 18, 20, 30, 220);
        var price = Raster("Price", 43, 20, 60, 220);
        var s = Session(photo, icon, price);
        Select(s, icon, price);
        var row = ApplyProposal(s);
        Assert.Equal(LayoutDirection.Row, row.AutoLayout!.Direction);
        s.ClearLayerSelection();
        var title = s.AddText(new(32, 145), new TextStyle { Text = "Coffee", Size = 23 });
        var sub = s.AddText(new(32, 185), new TextStyle { Text = "Light roast", Size = 14 });
        Select(s, photo, title, sub, row);
        var card = ApplyProposal(s);
        s.SetAutoLayout(card, card.AutoLayout! with { Gap = 12 });
        Assert.Equal(LayoutDirection.Column, card.AutoLayout!.Direction);
        var priceX = price.Transform.X;
        s.SetTransform(icon, icon.Transform with { Width = 32 });
        Assert.Equal(priceX + 14, price.Transform.X);
        Assert.True(s.SetAutoLayoutItem(title, s.LayoutItemSettings(title) with { WidthMode = LayoutSizeMode.Fill }));
        var old = EditorSession.LayoutBounds(sub).Top;
        var photos = photo.Pixels;
        var edit = Assert.IsType<Composa.Text.TextEditor>(s.EditText(title));
        edit.Insert(" from the southern highlands");
        Assert.True(EditorSession.LayoutBounds(sub).Top > old);
        Assert.Same(photos, photo.Pixels);
        Assert.Equal(title.Pixels!.Height, edit.Layout.Height);
        Assert.True(s.FinishText());
        var bounds = EditorSession.LayoutBounds(card);
        using var data = new MemoryStream();
        ProjectFile.Write(s.Document, data);
        data.Position = 0;
        var loaded = new EditorSession(ProjectFile.Read(data));
        var loadedCard = Assert.IsType<Layer>(loaded.Document.Find(card.Id));
        var loadedSub = Assert.IsType<Layer>(loaded.Document.Find(sub.Id));
        var loadedTitle = Assert.IsType<Layer>(loaded.Document.Find(title.Id));
        Assert.Equal(bounds, EditorSession.LayoutBounds(loadedCard));
        Assert.Equal(12, loadedCard.AutoLayout!.Gap);
        Assert.True(loadedTitle.Text!.AutoBoxHeight);
        using var before = s.Flatten();
        using var after = loaded.Flatten();
        Assert.Equal(before.Bytes, after.Bytes);
        var moved = loadedSub.Transform.Y;
        loaded.SelectLayer(loadedTitle.Id);
        loaded.ChangeTextStyle(style => style with { Text = "Coffee" });
        Assert.True(loadedSub.Transform.Y < moved);
    }

    [Fact]
    public void Image_replacement_preserves_fixed_frame_and_reflows_at_content_size()
    {
        var photo = Raster("Photo", 120, 70, 20, 20);
        var caption = Raster("Caption", 120, 15, 20, 102);
        var s = Session(photo, caption);
        Select(s, photo, caption);
        var group = ApplyProposal(s);
        Assert.True(s.SetAutoLayoutItem(photo, new(LayoutSizeMode.Fixed, LayoutSizeMode.Fixed, 120, 70, LayoutImageFit.Crop)));
        var frame = photo.LayoutFrame;
        var originalCaption = caption.Transform;
        using var shape = Pixels.NewColor(50, 100);
        shape.Erase(SKColors.Blue);
        var portrait = Pixels.Clone(shape);
        Assert.True(s.ReplaceLayoutImage(photo, portrait));
        Assert.Equal(frame, photo.LayoutFrame);
        Assert.Equal(originalCaption, caption.Transform);
        Assert.Same(portrait, photo.Pixels);
        Assert.Equal(2, photo.Transform.Height / photo.Transform.Width);
        using (var result = s.Flatten())
        {
            Assert.Equal(SKColors.Blue, result.GetPixel(50, 22));
            Assert.Equal((byte)0, result.GetPixel(50, 18).Alpha);
        }
        Assert.True(s.SetAutoLayoutItem(photo, s.LayoutItemSettings(photo) with { WidthMode = LayoutSizeMode.Content, HeightMode = LayoutSizeMode.Content }));
        Assert.Null(photo.LayoutFrame);
        Assert.Equal(240, photo.Transform.Height);
        Assert.Equal(272, caption.Transform.Y);
        Assert.Equal(12, group.AutoLayout!.Gap);
    }

    [Fact]
    public void Keeps_clipping_units_and_masks_and_requires_a_complete_chain()
    {
        var photo = Raster("Base", 105, 75, 20, 20);
        var clip = Raster("Highlight", 70, 30, 30, 25);
        clip.Clipped = true;
        photo.Mask = Pixels.NewMask(105, 75, 255);
        var mask = photo.Mask;
        var caption = Raster("Caption", 90, 20, 20, 107);
        var s = Session(photo, clip, caption);
        Select(s, photo, caption);
        Assert.Null(s.ProposeAutoLayout(out var reason));
        Assert.Contains("clipping base", reason);
        Assert.False(s.CanUndo);
        Select(s, photo, clip, caption);
        var group = ApplyProposal(s);
        var order = s.LayoutElements(group);
        Assert.Equal(2, order.Count);
        Assert.Equal(clip.Id, Assert.Single(order[0].Companions).Id);
        Assert.True(s.ReorderAutoLayout(group, photo.Id, 1));
        Assert.True(clip.Clipped);
        Assert.Same(mask, photo.Mask);
        Assert.Equal(5, clip.Transform.Y - photo.Transform.Y);
        Assert.Equal(group.Children.IndexOf(photo) + 1, group.Children.IndexOf(clip));
    }

    private static Layer ApplyProposal(EditorSession s)
    {
        var proposal = Assert.IsType<AutoLayoutProposal>(s.ProposeAutoLayout(out var problem));
        Assert.Null(problem);
        Assert.True(s.CreateAutoLayout(proposal));
        return Assert.IsType<Layer>(s.ActiveLayer);
    }

    private static EditorSession Session(params Layer[] layers)
    {
        var doc = new Document(420, 430);
        doc.Layers.AddRange(layers);
        return new EditorSession(doc);
    }

    private static void Select(EditorSession s, params Layer[] layers)
    {
        s.SelectLayer(layers[0].Id);
        foreach (var layer in layers.Skip(1)) s.SelectLayer(layer.Id, extend: true);
    }

    private static Layer Raster(string name, int width, int height, int x, int y)
    {
        var pixels = Pixels.NewColor(width, height);
        pixels.Erase(SKColors.DarkOrange);
        return Layer.Raster(name, pixels, x, y);
    }
}
