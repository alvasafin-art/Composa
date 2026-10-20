using Composa.ColorManagement;
using Composa.Editing;
using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class LinkedSmartObjectTests
{
    private static SmartObjectLink MissingLink() => new(Path.Combine(Path.GetTempPath(), "missing-composa-" + Guid.NewGuid().ToString("N") + ".cmps"), 1234, DateTime.UtcNow, new string('a', 64));

    [Fact]
    public void Place_retains_linked_source_profile_and_depth_and_project_opens_the_cached_document_offline()
    {
        var content = new Document(40, 30) { Depth = ColorDepth.Sixteen, Profile = RgbProfile.DisplayP3 };
        var pixels = Pixels.NewWorkingColor(40, 30, content.Depth, content.Profile); pixels.Erase(SKColors.Green);
        var inner = Layer.Raster("Original", pixels); content.InsertAboveActive(inner);
        var session = EditorSession.NewCanvas(80, 60); var link = MissingLink(); var layer = session.PlaceSmartObject(content, "Linked", link);
        Assert.Equal(link.Validated(), layer.SmartObject!.Link); Assert.Equal(ColorDepth.Eight, Pixels.DepthOf(layer.Pixels!)); Assert.Equal(RgbProfile.Srgb, Pixels.ProfileOf(layer.Pixels!));
        var original = layer.SmartObject.OpenDocument(); Assert.Equal(content.Profile, original.Profile); Assert.Equal(ColorDepth.Sixteen, original.Depth); Assert.Same(pixels, original.ActiveLayer!.Pixels);
        session.DuplicateSelectedLayers(); Assert.Same(layer.SmartObject, session.ActiveLayer!.SmartObject);
        using var stream = new MemoryStream(); ProjectFile.Write(session.Document, stream); stream.Position = 0;
        var restored = ProjectFile.Read(stream); var copies = restored.AllLayers().Where(l => l.SmartObject != null).ToArray();
        Assert.Equal(2, copies.Length); Assert.Same(copies[0].SmartObject, copies[1].SmartObject); Assert.Equal(link.Validated(), copies[0].SmartObject!.Link);
        using var a = session.Flatten(); using var b = DocumentRenderer.Flatten(restored); Assert.Equal(a.Bytes, b.Bytes);
    }

    [Fact]
    public void Embed_and_relink_change_only_metadata_and_undo_restores_the_shared_revision()
    {
        var source = EditorSession.NewCanvas(40, 30, SKColors.Red); var session = EditorSession.NewCanvas(80, 60);
        var layer = session.PlaceSmartObject(source.Document, "Linked", MissingLink()); session.DuplicateSelectedLayers();
        var expected = layer.SmartObject!; var pixels = layer.Pixels; session.MarkSaved("example.cmps");
        session.SetSmartObjectLink(expected, null); Assert.True(session.IsModified);
        Assert.All(session.Document.AllLayers().Where(l => l.SmartObject != null), l => { Assert.Null(l.SmartObject!.Link); Assert.Same(pixels, l.Pixels); });
        session.Undo(); Assert.False(session.IsModified); layer = session.Document.Find(layer.Id)!; Assert.Same(expected, layer.SmartObject);
        session.SetSmartObjectLink(expected, MissingLink()); Assert.NotEqual(expected.Link, layer.SmartObject!.Link); Assert.Same(expected.Preview, layer.SmartObject.Preview);
    }

    [Fact]
    public void Replacement_keeps_perspective_and_independent_masks_for_every_shared_instance()
    {
        var source = EditorSession.NewCanvas(40, 30, SKColors.Red); var session = EditorSession.NewCanvas(120, 90);
        var layer = session.PlaceSmartObject(source.Document, "Linked", MissingLink()); session.DuplicateSelectedLayers(); var other = session.ActiveLayer!;
        layer.Transform = layer.Transform with { Distort = [0, 0, 5, 2, -4, 0, 2, -3], Rotation = 13 };
        session.SelectLayer(layer.Id); session.AddMask(layer); session.SetMaskLinked(layer, false); session.EditingMask = false;
        var mask = layer.Mask; var maskMatrix = layer.MaskMatrix; var corners = layer.Transform.Corners(40, 30);
        session.AddSmartFilter(layer, new() { Kind = FilterKind.GaussianBlur, Radius = 2 }); session.AddSmartFilterMask(layer, hideAll: true);
        var expected = layer.SmartObject!; var newContent = EditorSession.NewCanvas(80, 45, SKColors.Blue); var replacementLink = MissingLink();
        session.ReplaceSmartObject(expected, newContent.Document, replacementLink);
        Assert.Same(layer.SmartObject, other.SmartObject); Assert.Equal(replacementLink.Validated(), layer.SmartObject!.Link);
        Assert.Same(mask, layer.Mask); Assert.Equal(maskMatrix, layer.MaskMatrix);
        for (var i = 0; i < 4; i++)
        {
            var p = layer.Matrix.MapPoint((i is 1 or 2 ? 80 : 0) + layer.FilterPaddingX, (i >= 2 ? 45 : 0) + layer.FilterPaddingY);
            Assert.InRange(Math.Abs(corners[i].X - p.X), 0, .002); Assert.InRange(Math.Abs(corners[i].Y - p.Y), 0, .002);
        }
        Assert.Equal(SKColors.Blue, other.Pixels!.GetPixel(20, 15)); Assert.NotNull(layer.SmartFilterMask);
        session.Undo(); Assert.Same(expected, session.Document.Find(layer.Id)!.SmartObject);
    }

    [Fact]
    public void Editing_cached_linked_contents_marks_them_as_local_changes_until_a_disk_revision_is_adopted()
    {
        var source = EditorSession.NewCanvas(40, 30, SKColors.Red); var session = EditorSession.NewCanvas(80, 60);
        var layer = session.PlaceSmartObject(source.Document, "Linked", MissingLink()); var expected = layer.SmartObject!;
        var contents = new EditorSession(expected.OpenDocument()); contents.Fill(SKColors.Blue);
        session.UpdateSmartObject(expected, contents.Document); Assert.True(layer.SmartObject!.Link!.HasLocalEdits);
        Assert.Equal(expected.Link!.Sha256, layer.SmartObject.Link.Sha256);
        session.ReplaceSmartObject(layer.SmartObject, contents.Document, MissingLink()); Assert.False(layer.SmartObject!.Link!.HasLocalEdits);
    }

    [Fact]
    public void File_loader_keeps_precision_and_profile_and_detects_content_changes_even_with_the_same_stamp()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-linked-source-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using var image = Pixels.NewWorkingColor(32, 24, ColorDepth.Sixteen, RgbProfile.DisplayP3); var pixels = new PixelBuffer(image);
            for (var y = 0; y < 24; y++) for (var x = 0; x < 32; x++) pixels.Write(x, y, new((30000 + x) / 65535f, .4f, .2f, 1));
            ImageFiles.Save(image, path, ExportFormat.Png);
            using var loaded = LinkedSmartObjectFile.Load(path);
            Assert.Equal(ColorDepth.Sixteen, loaded.Document.Depth); Assert.Equal(RgbProfile.DisplayP3, loaded.Document.Profile);
            Assert.Equal(LinkedSourceState.Current, LinkedSmartObjectFile.Probe(loaded.Link, verifyHash: true).State);
            File.SetLastWriteTimeUtc(path, loaded.Link.LastWriteUtc.AddSeconds(5));
            var touched = LinkedSmartObjectFile.Probe(loaded.Link);
            Assert.Equal(LinkedSourceState.Current, touched.State); Assert.NotEqual(loaded.Link.LastWriteUtc, touched.Revision!.LastWriteUtc);
            var decoded = new PixelBuffer(loaded.Document.ActiveLayer!.Pixels!);
            for (var x = 0; x < 32; x++) Assert.InRange(Math.Abs(pixels.Read(x, 10).R - decoded.Read(x, 10).R), 0, 1f / 65535);
            var session = EditorSession.NewCanvas(80, 60); var layer = session.PlaceSmartObject(loaded.Document, "Linked", loaded.Link); loaded.Adopt();
            var data = File.ReadAllBytes(path); data[^20] ^= 1; File.WriteAllBytes(path, data); File.SetLastWriteTimeUtc(path, loaded.Link.LastWriteUtc);
            Assert.Equal(LinkedSourceState.Modified, LinkedSmartObjectFile.Probe(loaded.Link, verifyHash: true).State);
            Assert.Equal(LinkedSourceState.LocalEdits, LinkedSmartObjectFile.Probe(loaded.Link with { HasLocalEdits = true }, verifyHash: true).State);
            File.Delete(path); Assert.Equal(LinkedSourceState.Missing, LinkedSmartObjectFile.Probe(loaded.Link).State);
            Assert.Equal(ColorDepth.Sixteen, layer.SmartObject!.OpenDocument().Depth); Assert.True(layer.Pixels!.Handle != IntPtr.Zero);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unadopted_file_content_is_disposed_but_opening_a_native_project_does_not_follow_its_links()
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-linked-source-" + Guid.NewGuid().ToString("N") + ".cmps");
        try
        {
            var source = EditorSession.NewCanvas(20, 16, SKColors.Red); var parent = EditorSession.NewCanvas(40, 32);
            parent.PlaceSmartObject(source.Document, "Offline", MissingLink()); ProjectFile.Save(parent.Document, path);
            var loaded = LinkedSmartObjectFile.Load(path); var image = loaded.Document.ActiveLayer!.Pixels!;
            Assert.NotNull(loaded.Document.ActiveLayer.SmartObject!.Link); Assert.True(image.Handle != IntPtr.Zero);
            loaded.Dispose(); Assert.Equal(IntPtr.Zero, image.Handle);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(".cmps")]
    [InlineData(".png")]
    [InlineData(".psd")]
    public void Saving_a_linked_revision_preserves_high_precision_profile_and_refuses_external_conflicts(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "composa-linked-save-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            var editor = EditorSession.NewCanvas(32, 24, new SKColor(100, 120, 140), depth: ColorDepth.Sixteen); editor.AssignColorProfile(RgbProfile.DisplayP3);
            var first = LinkedSmartObjectFile.Save(editor.Document.Clone(), path);
            Assert.Equal(LinkedSourceState.Current, LinkedSmartObjectFile.Probe(first, verifyHash: true).State);
            using (var loaded = LinkedSmartObjectFile.Load(path)) { Assert.Equal(ColorDepth.Sixteen, loaded.Document.Depth); Assert.Equal(RgbProfile.DisplayP3, loaded.Document.Profile); }
            editor.Fill(SKColors.Blue); var snapshot = editor.Document.Clone(); var originalPixels = snapshot.ActiveLayer!.Pixels!.Bytes;
            var second = LinkedSmartObjectFile.Save(snapshot, path, first);
            Assert.NotEqual(first.Sha256, second.Sha256); Assert.Equal(LinkedSourceState.Current, LinkedSmartObjectFile.Probe(second, verifyHash: true).State);
            Assert.Equal(originalPixels, snapshot.ActiveLayer.Pixels.Bytes);
            var currentFile = File.ReadAllBytes(path);
            Assert.Throws<IOException>(() => LinkedSmartObjectFile.Save(snapshot, path, first)); Assert.Equal(currentFile, File.ReadAllBytes(path));
            Assert.Throws<IOException>(() => LinkedSmartObjectFile.Save(snapshot, path)); Assert.Equal(currentFile, File.ReadAllBytes(path));
            File.Delete(path); Assert.Throws<IOException>(() => LinkedSmartObjectFile.Save(snapshot, path, second)); Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Source_save_does_not_write_PNG_data_to_unknown_extensions_or_overwrite_a_merged_PSD()
    {
        var prefix = Path.Combine(Path.GetTempPath(), "composa-linked-save-" + Guid.NewGuid().ToString("N")); var path = prefix + ".psd"; var svg = prefix + ".svg";
        try
        {
            var editor = EditorSession.NewCanvas(20, 16, SKColors.Red); var link = LinkedSmartObjectFile.Save(editor.Document, path);
            var original = File.ReadAllBytes(path);
            Assert.Throws<InvalidOperationException>(() => LinkedSmartObjectFile.Save(editor.Document, path, link with { UseMergedAppearance = true }, allowConversion: true));
            Assert.Equal(original, File.ReadAllBytes(path));
            File.WriteAllText(svg, "<svg/>"); Assert.Throws<InvalidDataException>(() => LinkedSmartObjectFile.Save(editor.Document, svg, overwrite: true)); Assert.Equal("<svg/>", File.ReadAllText(svg));
            Assert.Empty(LinkedSmartObjectFile.SaveConversions(editor.Document, prefix + ".jpg"));
            editor.SelectRect(new(0, 0, 3, 3)); editor.ClearSelection(); editor.Deselect();
            Assert.Contains(LinkedSmartObjectFile.SaveConversions(editor.Document, prefix + ".jpg"), c => c.Message.Contains("transparency"));
        }
        finally { File.Delete(path); File.Delete(svg); }
    }
}
