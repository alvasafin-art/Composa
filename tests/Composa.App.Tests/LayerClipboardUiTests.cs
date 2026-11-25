using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

public class LayerClipboardUiTests
{
    [AvaloniaFact]
    public async Task Ctrl_copy_pastes_a_group_and_a_layer_once_in_the_same_tab_and_in_another_tab()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        var source = EditorSession.NewCanvas(420, 300, SKColors.White);
        window.Show();
        window.AddSession(source);
        try
        {
            var photo = AddSquare(source, "Photo", new SKPoint(90, 90));
            source.SetEffects(photo, new LayerEffects { Stroke = new StrokeEffect { Size = 3 } });
            source.AddMask(photo, hideAll: true, activateLayer: false);
            source.SetMaskLinked(photo, false);
            source.SetMaskTransform(photo, LayerTransform.Identity(30, 30) with { X = 70, Y = 76 });
            source.BeginText(new SKPoint(130, 100)).Insert("Editable");
            source.FinishText();
            var text = source.ActiveLayer!;
            var badge = AddSquare(source, "Badge", new SKPoint(250, 170));
            source.SelectLayer(photo.Id);
            source.SelectLayer(text.Id, extend: true);
            source.GroupSelectedLayers();
            var group = source.ActiveLayer!;
            source.SelectLayer(badge.Id);
            source.SelectLayer(group.Id, extend: true);
            source.SelectLayer(text.Id, extend: true); // The selected child must appear only inside the group copy.
            window.SelectTool(Tool.Move);
            window.Canvas.Focus();
            Dispatcher.UIThread.RunJobs();

            Key(window, PhysicalKey.C);
            Assert.Equal(2, EditorSession.CopiedLayers?.Layers.Count);
            Assert.NotNull(await window.Clipboard!.TryGetBitmapAsync());
            var beforePaste = source.History.CurrentId;
            Key(window, PhysicalKey.V);
            await Until(() => source.History.UndoName == "Paste Layers");
            var same = CheckCopies(source, group, photo, text, badge);
            Assert.Equal(photo.Transform, same.Photo.Transform);
            Assert.Equal(photo.MaskTransform, same.Photo.MaskTransform);
            Assert.Equal(text.Transform, same.Text.Transform);
            Assert.Equal(badge.Transform, same.Badge.Transform);
            Key(window, PhysicalKey.Z);
            Assert.Equal(beforePaste, source.History.CurrentId);
            Assert.Equal(3, source.Document.Layers.Count);

            var target = EditorSession.NewCanvas(600, 500, SKColors.White);
            var beforeTargetPaste = target.History.CurrentId;
            window.AddSession(target);
            window.Canvas.Focus();
            Dispatcher.UIThread.RunJobs();
            Key(window, PhysicalKey.V);
            await Until(() => target.History.UndoName == "Paste Layers");
            var other = CheckCopies(target, group, photo, text, badge);
            var copiedPositions = new[] { other.Photo.Transform, other.Text.Transform, other.Badge.Transform, other.Photo.MaskTransform! };
            var originalPositions = new[] { photo.Transform, text.Transform, badge.Transform, photo.MaskTransform! };
            var dx = other.Photo.Transform.X - photo.Transform.X;
            var dy = other.Photo.Transform.Y - photo.Transform.Y;
            Assert.NotEqual((0d, 0d), (dx, dy));
            for (var i = 0; i < copiedPositions.Length; i++)
                Assert.Equal(originalPositions[i].Translated(dx, dy), copiedPositions[i]);
            Assert.NotEqual(same.Text.Id, other.Text.Id);
            Key(window, PhysicalKey.Z);
            Assert.Equal(beforeTargetPaste, target.History.CurrentId);
            Assert.Single(target.Document.Layers);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Ctrl_copy_preserves_pixel_selection_and_editing_text_and_accepts_a_new_external_bitmap()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        var session = EditorSession.NewCanvas(420, 300, SKColors.White);
        window.Show();
        window.AddSession(session);
        try
        {
            var square = AddSquare(session, "Square", new SKPoint(110, 110));
            AddSquare(session, "Other", new SKPoint(250, 120));
            session.SelectLayer(square.Id, extend: true);
            session.SelectRect(new SKRect(105, 106, 115, 117));
            window.SelectTool(Tool.Move);
            window.Canvas.Focus();
            Dispatcher.UIThread.RunJobs();
            Key(window, PhysicalKey.C);
            Key(window, PhysicalKey.V);
            await Until(() => session.History.UndoName == "Paste");
            Assert.Equal((105d, 106d, 10, 11), (session.ActiveLayer!.Transform.X, session.ActiveLayer.Transform.Y,
                session.ActiveLayer.Pixels!.Width, session.ActiveLayer.Pixels.Height));
            Assert.Equal(SKColors.Red, session.ActiveLayer.Pixels.GetPixel(0, 0));

            session.Deselect();
            Key(window, PhysicalKey.C); // A newer external image must supersede these whole-layer data.
            using var external = new WriteableBitmap(new Avalonia.PixelSize(12, 8), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            await window.Clipboard!.SetBitmapAsync(external);
            Key(window, PhysicalKey.V);
            await Until(() => session.ActiveLayer?.Pixels?.Width == 12);
            Assert.Equal((204d, 146d), (session.ActiveLayer!.Transform.X, session.ActiveLayer.Transform.Y));

            window.SelectTool(Tool.Text);
            var editor = session.BeginText(new SKPoint(50, 60));
            editor.Insert("Live words");
            editor.SelectAll();
            window.Canvas.Focus();
            Key(window, PhysicalKey.C);
            Assert.Equal("Live words", await window.Clipboard!.TryGetTextAsync());
            editor.MoveToDocumentEdge(end: true, select: false);
            Key(window, PhysicalKey.V);
            await Until(() => session.ActiveLayer?.Text?.Text == "Live wordsLive words");
            Assert.Same(editor, session.TextEdit);
            session.FinishText();
        }
        finally { window.Close(); }
    }

    private static (Layer Photo, Layer Text, Layer Badge) CheckCopies(EditorSession session, Layer originalGroup,
        Layer originalPhoto, Layer originalText, Layer originalBadge)
    {
        var group = session.Document.Layers[^2];
        var badge = session.Document.Layers[^1];
        Assert.True(group.IsGroup);
        Assert.Equal(originalBadge.Name, badge.Name);
        Assert.Equal([originalPhoto.Name, originalText.Name], group.Children.Select(layer => layer.Name));
        var (photo, text) = (group.Children[0], group.Children[1]);
        Assert.NotEqual(originalGroup.Id, group.Id);
        Assert.NotEqual(originalBadge.Id, badge.Id);
        Assert.NotEqual(originalPhoto.Id, photo.Id);
        Assert.NotEqual(originalText.Id, text.Id);
        Assert.Equal("Editable", text.Text!.Text);
        Assert.Equal(originalPhoto.Effects, photo.Effects);
        Assert.NotNull(photo.Mask);
        Assert.Equal(originalPhoto.Mask!.GetPixel(0, 0), photo.Mask.GetPixel(0, 0));
        Assert.False(photo.MaskLinked);
        Assert.Equal(new[] { group.Id, badge.Id }, session.Document.SelectionOrder);
        return (photo, text, badge);
    }

    private static Layer AddSquare(EditorSession session, string name, SKPoint center)
    {
        var pixels = Pixels.NewColor(30, 30);
        pixels.Erase(SKColors.Red);
        return session.AddImageLayer(name, pixels, center);
    }

    private static void Key(MainWindow window, PhysicalKey key) => window.KeyPressQwerty(key, RawInputModifiers.Control);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition(), "The clipboard command did not finish.");
    }
}
