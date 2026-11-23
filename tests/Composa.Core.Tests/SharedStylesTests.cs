using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public sealed class SharedStylesTests
{
    [Fact]
    public void Eight_prices_reflow_together_with_local_color_and_one_undo()
    {
        var doc = new Document(500, 410);
        var session = new EditorSession(doc);
        var series = new List<(Layer Price, Layer Footer, Layer Card)>();
        for (var i = 0; i < 8; i++)
        {
            var x = 20 + i % 4 * 120;
            var y = 20 + i / 4 * 160;
            var text = new TextStyle { Text = (690 + i * 10) + " ₽", Size = 20, Color = 0xff392b21 };
            var price = Layer.Raster("Price " + i, EditorSession.RenderText(text), x, y);
            price.Text = text;
            var footer = Layer.Raster("Footer", Pixels.NewColor(30, 8), x, price.ControlBounds.Bottom + 12);
            var card = Layer.Group("Card " + i);
            card.AutoLayout = new(x, y, LayoutDirection.Column, LayoutAlignment.Start, 12, new());
            card.Children.AddRange([footer, price]);
            doc.Layers.Add(card);
            series.Add((price, footer, card));
        }

        var preset = Assert.IsType<LayerStyleDefinition>(session.CreateLayerStyle(series[0].Price, LayerStyleKind.Text, "Prices"));
        foreach (var item in series.Skip(1)) Assert.True(session.ApplyLayerStyle(item.Price, preset.Id));
        Assert.Equal(8, session.StyleLinkCount(preset));
        session.SelectLayer(series[3].Price.Id);
        session.SetTextColor(0xffb42129);
        Assert.Equal(StyleFields.Color, session.StyleOverrides(series[3].Price, LayerStyleKind.Text));
        var undo = session.History.CurrentId;
        var start = series.Select(item => item.Footer.Transform.Y).ToArray();
        var wording = series.Select(item => item.Price.Text!.Text).ToArray();
        Assert.True(session.UpdateLayerStyle(preset with { Text = preset.Text! with { Size = 30 } }));
        for (var i = 0; i < series.Count; i++)
        {
            var (price, footer, card) = series[i];
            Assert.Equal(wording[i], price.Text!.Text);
            Assert.Equal(30, price.Text.Size);
            Assert.Equal(i == 3 ? 0xffb42129u : 0xff392b21u, price.Text.Color);
            Assert.Equal(12, card.AutoLayout!.Gap);
            Assert.True(footer.Transform.Y > start[i]);
        }
        session.Undo();
        Assert.Equal(undo, session.History.CurrentId);
        Assert.All(series, item => Assert.Equal(20, session.Document.Find(item.Price.Id)!.Text!.Size));
        Assert.Equal(start, series.Select(item => session.Document.Find(item.Footer.Id)!.Transform.Y));
        session.Redo();
        Assert.All(series, item => Assert.Equal(30, session.Document.Find(item.Price.Id)!.Text!.Size));

        using var data = new MemoryStream();
        ProjectFile.Write(session.Document, data);
        data.Position = 0;
        var reopened = new EditorSession(ProjectFile.Read(data));
        var saved = Assert.Single(reopened.Document.Styles.Values);
        var changed = reopened.Document.Find(series[3].Price.Id)!;
        Assert.Equal(8, reopened.StyleLinkCount(saved));
        Assert.Equal(StyleFields.Color, reopened.StyleOverrides(changed, LayerStyleKind.Text));
        Assert.True(reopened.ResetStyleFields(changed, LayerStyleKind.Text, StyleFields.Color));
        Assert.Equal(0xff392b21u, changed.Text!.Color);
        var pixels = changed.Pixels;
        Assert.True(reopened.UnlinkLayerStyle(changed, LayerStyleKind.Text));
        Assert.Same(pixels, changed.Pixels);
        Assert.Equal(7, reopened.StyleLinkCount(saved));
    }

    [Fact]
    public void Appearance_updates_outline_width_preserving_local_color_mask_and_geometry()
    {
        var s = EditorSession.NewCanvas(360, 260);
        var rectangle = new ShapeStyle(ShapeKind.RoundedRectangle, 0xffe6d9be, 7) { Stroke = 0xff58351d, StrokeWidth = 2 };
        var shapes = Enumerable.Range(0, 4).Select(i => s.AddShape(rectangle,
            SKRect.Create(30 + i * 65, 40 + i * 17, 51 + i, 44 + i))!).ToArray();
        s.AddMask(shapes[1]);
        var mask = shapes[1].Mask;
        var transform = shapes[1].Transform;
        var preset = Assert.IsType<LayerStyleDefinition>(s.CreateLayerStyle(shapes[0], LayerStyleKind.Appearance, "Wood outline"));
        foreach (var shape in shapes.Skip(1)) Assert.True(s.ApplyLayerStyle(shape, preset.Id));
        s.ChangeShapeStyle(shapes[1], shapes[1].Shape! with { Stroke = 0xff326537 });
        Assert.Equal(StyleFields.Stroke, s.StyleOverrides(shapes[1], LayerStyleKind.Appearance));
        var appearance = preset.Appearance!;
        var state = s.History.CurrentId;
        Assert.True(s.UpdateLayerStyle(preset with
        {
            Appearance = appearance with
            {
                Paint = appearance.Paint! with { StrokeWidth = 6 },
                Effects = new LayerEffects { Shadow = new() { Blur = 9 } }
            }
        }));
        Assert.All(shapes, shape => Assert.Equal(6, shape.Shape!.StrokeWidth));
        Assert.All(shapes, shape => Assert.Equal(9, shape.Effects!.Shadow!.Blur));
        Assert.Equal(0xff326537u, shapes[1].Shape!.Stroke);
        Assert.Equal(7, shapes[1].Shape!.CornerRadius);
        Assert.Equal(transform, shapes[1].Transform);
        Assert.Same(mask, shapes[1].Mask);

        using var stream = new MemoryStream();
        ProjectFile.Write(s.Document, stream);
        stream.Position = 0;
        var reopened = new EditorSession(ProjectFile.Read(stream));
        var current = Assert.Single(reopened.Document.Styles.Values);
        Assert.Equal(4, reopened.StyleLinkCount(current));
        var restoredShape = reopened.Document.Find(shapes[1].Id)!;
        Assert.Equal(StyleFields.Stroke, reopened.StyleOverrides(restoredShape, LayerStyleKind.Appearance));
        Assert.True(reopened.ResetStyleFields(restoredShape, LayerStyleKind.Appearance, StyleFields.Stroke));
        Assert.Equal(0xff58351du, restoredShape.Shape!.Stroke);
        Assert.Equal(6, restoredShape.Shape.StrokeWidth);
        s.Undo();
        Assert.Equal(state, s.History.CurrentId);
        Assert.All(shapes, shape => Assert.Equal(2, s.Document.Find(shape.Id)!.Shape!.StrokeWidth));
        Assert.Equal(0xff326537u, s.Document.Find(shapes[1].Id)!.Shape!.Stroke);
    }

    [Fact]
    public void A_local_value_stays_local_when_the_shared_value_matches_it_in_between()
    {
        var s = EditorSession.NewCanvas(500, 250);
        var price = s.AddText(new SKPoint(40, 50), new TextStyle { Text = "690 ₽", Size = 20 });
        s.AddMask(price);
        var mask = price.Mask;
        var matrix = price.MaskMatrix;
        var position = (price.Transform.X, price.Transform.Y);
        var preset = Assert.IsType<LayerStyleDefinition>(s.CreateLayerStyle(price, LayerStyleKind.Text, "Price"));
        s.SelectLayer(price.Id);
        s.SetTextColor(0xff005599);
        Assert.True(s.UpdateLayerStyle(preset with { Text = preset.Text! with { Color = 0xff005599, Size = 29 } }));
        Assert.True(s.UpdateLayerStyle(preset with { Text = preset.Text! with { Color = 0xff990055, Size = 32 } }));
        Assert.Equal(0xff005599u, price.Text!.Color);
        Assert.Equal(32, price.Text.Size);
        Assert.Equal(position, (price.Transform.X, price.Transform.Y));
        Assert.Same(mask, price.Mask);
        Assert.Equal(matrix.MapPoint(1, 1).X, price.MaskMatrix.MapPoint(1, 1).X, 3);
        Assert.Equal(matrix.MapPoint(1, 1).Y, price.MaskMatrix.MapPoint(1, 1).Y, 3);
    }

    [Fact]
    public void Group_uses_opacity_and_inapplicable_fields_do_not_change_its_children()
    {
        var s = EditorSession.NewCanvas(400, 220);
        var layer = s.AddShape(new ShapeStyle(ShapeKind.Rectangle, 0xff112233, 0), SKRect.Create(10, 22, 80, 66), opacity: .7)!;
        var style = Assert.IsType<LayerStyleDefinition>(s.CreateLayerStyle(layer, LayerStyleKind.Appearance, "Muted"));
        var group = Layer.Group("Folder");
        var child = Layer.Raster("Independent", Pixels.NewColor(42, 30), 90, 38);
        group.Children.Add(child);
        s.Document.Layers.Add(group);
        var childTransform = child.Transform;
        Assert.True(s.ApplyLayerStyle(group, style.Id));
        Assert.Equal(.7, group.Opacity, 3);
        Assert.Null(group.Shape);
        Assert.Null(group.Effects);
        Assert.Equal(childTransform, child.Transform);
        Assert.Equal(StyleFields.None, s.StyleOverrides(group, LayerStyleKind.Appearance));
        s.SetLayerLocks(layer, LayerLocks.All);
        var revision = s.Revision;
        Assert.False(s.UpdateLayerStyle(style with { Appearance = style.Appearance! with { Opacity = .5 } }));
        Assert.Equal(revision, s.Revision);
        Assert.Equal(.7, group.Opacity, 3);
    }

    [Fact]
    public void Deleting_a_shared_style_preserves_appearance_and_undo_restores_the_bindings()
    {
        var session = EditorSession.NewCanvas(400, 240);
        var first = session.AddText(new(15, 20), new TextStyle { Text = "One", Size = 30 });
        var second = session.AddText(new(15, 90), new TextStyle { Text = "Two", Size = 26 });
        var style = session.CreateLayerStyle(first, LayerStyleKind.Text, "Heading")!;
        Assert.True(session.ApplyLayerStyle(second, style.Id));
        session.SelectLayer(second.Id);
        session.SetTextColor(0xff225588);
        session.SetLayerLocks(first, LayerLocks.All);
        var firstPixels = first.Pixels;
        var secondPixels = second.Pixels;
        var secondText = second.Text;
        var revision = session.Revision;

        Assert.True(session.DeleteLayerStyle(style.Id));
        Assert.False(session.DeleteLayerStyle(style.Id));
        Assert.Equal(revision + 1, session.Revision);
        Assert.Empty(session.Document.Styles);
        Assert.Null(first.SharedTextStyle);
        Assert.Null(second.SharedTextStyle);
        Assert.Same(firstPixels, first.Pixels);
        Assert.Same(secondPixels, second.Pixels);
        Assert.Equal(secondText, second.Text);
        Assert.Equal(LayerLocks.All, first.Locks);
        session.Undo();
        Assert.Equal(style, Assert.Single(session.Document.Styles.Values));
        Assert.Equal(2, session.StyleLinkCount(style));
        Assert.Equal(StyleFields.Color, session.StyleOverrides(session.Document.Find(second.Id)!, LayerStyleKind.Text));
        session.Redo();

        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        stream.Position = 0;
        var reopened = ProjectFile.Read(stream);
        Assert.Empty(reopened.Styles);
        Assert.Null(reopened.Find(second.Id)!.SharedTextStyle);
        Assert.Equal(secondText, reopened.Find(second.Id)!.Text);
    }
}
