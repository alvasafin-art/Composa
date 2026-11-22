using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public sealed class LibraryTests
{
    [Fact]
    public void Card_keeps_styles_editable_layout_and_masks_without_the_library()
    {
        var folder = Path.Combine(Path.GetTempPath(), "composa-library-" + Guid.NewGuid().ToString("N"));
        var library = new ElementLibrary(Path.Combine(folder, "Library"));
        try
        {
            var source = EditorSession.NewCanvas(440, 340);
            var photo = source.AddImageLayer("Photo", Color(140, 85, SKColors.SaddleBrown), new(120, 70), fit: false);
            var heading = source.AddText(new(50, 124), new TextStyle { Text = "Coffee", Size = 20, Color = 0xff40271b });
            var price = source.AddText(new(50, 166), new TextStyle { Text = "690", Size = 15 });
            var icon = source.AddImageLayer("Icon", Color(18, 18, SKColors.Sienna), new(114, 174), fit: false);
            source.SelectLayer(price.Id);
            source.SelectLayer(icon.Id, extend: true);
            var row = Layout(source);
            source.SelectLayer(photo.Id);
            source.SelectLayer(heading.Id, extend: true);
            source.SelectLayer(row.Id, extend: true);
            var card = Layout(source);
            Assert.True(source.SetAutoLayoutItem(photo, new(LayoutSizeMode.Fixed, LayoutSizeMode.Fixed, 140, 85)));
            source.AddMask(photo);
            source.SetMaskLinked(photo, false);
            var appearance = source.CreateLayerStyle(photo, LayerStyleKind.Appearance, "Photo finish")!;
            var style = source.CreateLayerStyle(heading, LayerStyleKind.Text, "Heading")!;
            source.SelectLayer(heading.Id);
            source.SetTextColor(0xff634529);
            source.SelectLayer(card.Id);

            var capture = source.CaptureLibraryElement(out var embedded);
            var thumbnail = ElementLibrary.RenderThumbnail(capture);
            var entry = library.Save("Coffee card", capture, thumbnail, embedded);
            Assert.Equal(thumbnail, library.LoadThumbnail(entry));
            Assert.Equal(2, capture.Styles.Count);
            Assert.Single(library.List().Entries);

            var target = EditorSession.NewCanvas(680, 400, SKColors.White);
            var existing = target.AddText(new(10, 20), new TextStyle { Text = "Existing", Size = 14, Color = 0xff224466 });
            var existingStyle = target.CreateLayerStyle(existing, LayerStyleKind.Text, "Heading")!;
            var first = target.InsertLibraryElement(library.Load(entry), new(175, 205));
            var second = target.InsertLibraryElement(library.Load(entry), new(505, 205));
            Assert.Equal(3, target.Document.Styles.Count);
            var firstHeading = first.Children.Single(layer => layer.Text != null);
            var secondHeading = second.Children.Single(layer => layer.Text != null);
            Assert.Equal("Heading (2)", target.LinkedStyle(firstHeading, LayerStyleKind.Text)?.Name);
            Assert.Equal(firstHeading.SharedTextStyle, secondHeading.SharedTextStyle);
            Assert.Equal(StyleFields.Color, target.StyleOverrides(firstHeading, LayerStyleKind.Text));
            Assert.Equal(0xff634529u, firstHeading.Text!.Color);
            Assert.Equal(existingStyle.Id, existing.SharedTextStyle?.Id);
            Assert.Equal(14, existing.Text!.Size);
            Assert.Equal(0xff224466u, existing.Text.Color);
            Assert.Equal(appearance.Appearance, target.LinkedStyle(first.Children.Single(l => l.Mask != null), LayerStyleKind.Appearance)?.Appearance);
            Assert.Equal(row.AutoLayout!.Direction, second.Children.Single(l => l.IsGroup).AutoLayout!.Direction);
            Assert.Equal(505, EditorSession.LayoutBounds(second).MidX, precision: 0);

            target.Undo();
            Assert.Null(target.Document.Find(second.Id));
            Assert.Equal(3, target.Document.Styles.Count);
            target.Undo();
            Assert.Null(target.Document.Find(first.Id));
            Assert.Single(target.Document.Styles);
            target.Redo();
            target.Redo();
            first = target.Document.Find(first.Id)!;
            second = target.Document.Find(second.Id)!;

            var firstPhoto = first.Children.Single(layer => layer.Mask != null);
            var secondPhoto = second.Children.Single(layer => layer.Mask != null);
            Assert.Equal(photo.Mask!.Bytes, firstPhoto.Mask!.Bytes);
            var photoDelta = firstPhoto.Transform.X - photo.Transform.X;
            Assert.Equal(photoDelta, firstPhoto.MaskMatrix.TransX - photo.MaskMatrix.TransX, precision: 3);
            Assert.True(target.ReplaceLayoutImage(firstPhoto, Color(58, 100, SKColors.SteelBlue)));
            secondHeading = second.Children.Single(layer => layer.Text != null);
            var before = EditorSession.LayoutBounds(second);
            var editor = target.EditText(secondHeading)!;
            editor.Insert("\nHighland roast");
            Assert.True(target.FinishText());
            Assert.True(EditorSession.LayoutBounds(second).Height > before.Height);
            Assert.Equal("Coffee", first.Children.Single(layer => layer.Text != null).Text!.Text);
            Assert.Equal(140, secondPhoto.Pixels!.Width);

            var project = Path.Combine(folder, "B.cmps");
            ProjectFile.Save(target.Document, project);
            Directory.Move(library.Folder, Path.Combine(folder, "Offline"));
            var reopened = new EditorSession(ProjectFile.Load(project));
            using var beforeClose = target.Flatten();
            using var afterOpen = reopened.Flatten();
            Assert.Equal(beforeClose.Bytes, afterOpen.Bytes);
            Assert.Equal(3, reopened.Document.Styles.Count);
            var reopenPhoto = reopened.Document.Find(firstPhoto.Id)!;
            Assert.True(reopened.ReplaceLayoutImage(reopenPhoto, Color(65, 90, SKColors.Green)));
            reopened.SelectLayer(secondHeading.Id);
            Assert.NotNull(reopened.EditText(reopened.ActiveLayer!));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Archive_updates_are_atomic_and_nested_smart_object_instances_are_local()
    {
        var folder = Path.Combine(Path.GetTempPath(), "composa-library-" + Guid.NewGuid().ToString("N"));
        var library = new ElementLibrary(folder);
        try
        {
            var nestedSource = EditorSession.NewCanvas(28, 20, SKColors.Green);
            var middle = EditorSession.NewCanvas(32, 24);
            var link = new SmartObjectLink(Path.Combine(folder, "missing.cmps"), 123, DateTime.UtcNow, new string('a', 64));
            middle.PlaceSmartObject(nestedSource.Document, "Linked", link);
            var source = EditorSession.NewCanvas(100, 80);
            var objectLayer = source.PlaceSmartObject(middle.Document, "Embedded");
            source.DuplicateSelectedLayers();
            var twin = source.ActiveLayer!;
            source.SelectLayer(objectLayer.Id, extend: true);
            source.GroupSelectedLayers();
            var capture = source.CaptureLibraryElement(out var embedded);
            Assert.True(embedded);
            Assert.NotNull(twin.SmartObject!.OpenDocument().AllLayers().Single(l => l.IsSmartObject).SmartObject!.Link);
            var thumbnail = ElementLibrary.RenderThumbnail(capture);
            var entry = library.Save("Sample", capture, thumbnail, embedded);
            var renamed = library.Rename(entry, "Renamed");
            Assert.Equal(entry.Id, renamed.Id);
            Assert.Equal(thumbnail, library.LoadThumbnail(renamed));
            Assert.Throws<IOException>(() => library.Delete(entry));

            var failed = capture.Clone();
            failed.Layers[0].Opacity = double.NaN;
            Assert.Throws<ArgumentException>(() => library.Save("Interrupted", failed, thumbnail, embedded, renamed));
            Assert.Equal("Renamed", Assert.Single(library.List().Entries).Name);
            Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp-*"));

            var target = EditorSession.NewCanvas(320, 180);
            var element = library.Load(renamed);
            Assert.Null(element.Layers[0].Children[0].SmartObject!.OpenDocument().AllLayers().Single(l => l.IsSmartObject).SmartObject!.Link);
            var first = target.InsertLibraryElement(element);
            var second = target.InsertLibraryElement(element);
            var a = first.Children[0].SmartObject!;
            var b = second.Children[0].SmartObject!;
            Assert.Same(a, first.Children[1].SmartObject);
            Assert.NotEqual(a.Id, b.Id);
            Assert.NotEqual(a.OpenDocument().ActiveLayer!.SmartObject!.Id, b.OpenDocument().ActiveLayer!.SmartObject!.Id);
            target.UpdateSmartObject(a, EditorSession.NewCanvas(32, 24, SKColors.Orange).Document);
            Assert.Same(b, second.Children[0].SmartObject);
            Assert.NotSame(a, first.Children[0].SmartObject);
            var updated = library.Save("Renamed", capture, thumbnail, embedded, renamed);
            Assert.NotEqual(renamed.Revision, updated.Revision);
            library.Delete(updated);
            Assert.Empty(library.List().Entries);
            Assert.Equal(2, target.Document.Layers.Count(layer => layer.IsGroup));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    private static Layer Layout(EditorSession session)
    {
        var proposal = Assert.IsType<AutoLayoutProposal>(session.ProposeAutoLayout(out var problem));
        Assert.Null(problem);
        Assert.True(session.CreateAutoLayout(proposal));
        return session.ActiveLayer!;
    }

    private static SKBitmap Color(int width, int height, SKColor color)
    {
        var image = Pixels.NewColor(width, height);
        image.Erase(color);
        return image;
    }
}
