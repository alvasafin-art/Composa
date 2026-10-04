using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.IO.Psd;
using Composa.Model;
using Composa.Rendering;
using Composa.Text;
using SkiaSharp;
using Magick = ImageMagick;

namespace Composa.App.Tests;

public class PsdExportUiTests
{
    [AvaloniaFact]
    public async Task Native_text_and_shapes_save_and_open_through_the_window_without_conversion_dialogs()
    {
        var window = new MainWindow { Width = 1000, Height = 700 }; window.Show();
        var document = new Document(320, 240);
        var family = EditorSession.FontFamilies.FirstOrDefault(f => f == "Arial") ?? EditorSession.FontFamilies[0];
        var style = new TextStyle { Text = "Editable PSD", FontFamily = family, Size = 24 }.WithColor(0xFFFF0000, 0, 4);
        var text = Layer.Raster("Text", new TextLayout(style).Render(), 10, 10); text.Text = style;
        var shapeStyle = new ShapeStyle(ShapeKind.RoundedRectangle, 0xFF3060A0, 12) { Stroke = 0xFF102030, StrokeWidth = 4 };
        var shape = Layer.Raster("Shape", EditorSession.RenderShape(shapeStyle, 80, 50), 40, 100); shape.Shape = shapeStyle;
        document.Layers.Add(text); document.Layers.Add(shape); var session = new EditorSession(document); window.AddSession(session);
        // The fixture lives with the test assembly in the project's build output, never in the system temp folder.
        var path = Path.Combine(AppContext.BaseDirectory, "native-window-round-trip.psd");
        var save = window.SaveTo(session, path); await Pump(save); Assert.Null(await save); Assert.Empty(window.OwnedWindows);
        var open = window.OpenPath(path); await Pump(open); var loaded = await open;
        Assert.Empty(window.OwnedWindows); Assert.NotNull(loaded); Assert.Equal(path, loaded!.FilePath);
        Assert.Equal(style.Text, loaded.Document.Layers[0].Text!.Text); Assert.Equal(0xFFFF0000u, loaded.Document.Layers[0].Text!.ColorAt(1));
        var live = loaded.Document.Layers[1]; Assert.Equal(shapeStyle, live.Shape);
        loaded.ChangeShapeStyle(live, live.Shape! with { CornerRadius = 18 }); Assert.Equal(18, live.Shape!.CornerRadius);
    }

    [Fact]
    public void ImageMagick_reads_the_compatibility_images_of_native_layers_and_the_merged_preview()
    {
        Assert.NotNull(BundledImageMagick.TryLoad());
        var document = new Document(240, 180);
        var style = new TextStyle { Text = "PSD", FontFamily = EditorSession.FontFamilies[0], Size = 24 };
        var text = Layer.Raster("Type", new TextLayout(style).Render(), 10, 10); text.Text = style; document.Layers.Add(text);
        var s = new ShapeStyle(ShapeKind.RoundedRectangle, 0xFF3060A0, 13) { Stroke = 0xFFC83214, StrokeWidth = 6 };
        var shape = Layer.Raster("Shape", EditorSession.RenderShape(s, 80, 50), 40, 100); shape.Shape = s; document.Layers.Add(shape);
        using var stream = new MemoryStream(); PsdExport.Write(document, stream);
        using var images = new Magick.MagickImageCollection(); images.Read(stream.ToArray()); Assert.Equal(3, images.Count);
        using var preview = SKBitmap.Decode(images[0].ToByteArray(Magick.MagickFormat.Png)); using var expected = DocumentRenderer.Flatten(document);
        // A PNG decoder may return BGRA; compare premultiplied RGBA on both sides.
        using var normalized = Pixels.NewColor(preview.Width, preview.Height);
        using (var canvas = new SKCanvas(normalized)) canvas.DrawBitmap(preview, 0, 0);
        var a = expected.GetPixelSpan(); var b = normalized.GetPixelSpan(); Assert.Equal(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++) Assert.InRange(Math.Abs(a[i] - b[i]), 0, 2);
        Assert.Equal("Type", images[1].GetAttribute("label")); Assert.Equal("Shape", images[2].GetAttribute("label"));
        using var cached = SKBitmap.Decode(images[2].ToByteArray(Magick.MagickFormat.Png));
        Assert.Equal(new SKColor(s.Fill), cached.GetPixel(40, 25)); Assert.Equal(new SKColor(s.Stroke!.Value), cached.GetPixel(40, 1));
    }

    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = Pixels.NewColor(width, height); bitmap.Erase(color); return bitmap;
    }

    private static async Task Pump(Task task)
    {
        for (var i = 0; i < 800 && !task.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(task.IsCompleted); await task;
    }

    [Fact]
    public void ImageMagick_independently_reads_layers_masks_names_and_the_transparent_merged_preview()
    {
        Assert.NotNull(BundledImageMagick.TryLoad());
        var document = new Document(32, 24) { Resolution = 300 };
        var bottom = Layer.Raster("Bottom", Solid(8, 6, SKColors.Coral), -2, 4);
        var top = Layer.Raster("Top", Solid(12, 10, new SKColor(40, 120, 230, 128)), 10, 8);
        top.Mask = Pixels.NewMask(12, 10, 128);
        document.Layers.Add(bottom); document.Layers.Add(top);
        using var stream = new MemoryStream(); PsdExport.Write(document, stream);
        using var images = new Magick.MagickImageCollection(); images.Read(stream.ToArray());
        Assert.Equal(3, images.Count); // merged preview followed by separate source layers
        Assert.Equal("Bottom", images[1].GetAttribute("label"));
        Assert.Equal("Top", images[2].GetAttribute("label"));
        Assert.Equal(-2, images[1].Page.X); Assert.Equal(4, images[1].Page.Y);
        Assert.Equal(300, images[0].Density.X);
        Assert.NotNull(images[0].GetColorProfile());
        using var preview = SKBitmap.Decode(images[0].ToByteArray(Magick.MagickFormat.Png));
        using var expected = DocumentRenderer.Flatten(document);
        for (var y = 0; y < expected.Height; y++)
        for (var x = 0; x < expected.Width; x++)
        {
            var a = expected.GetPixel(x, y); var b = preview.GetPixel(x, y);
            Assert.InRange(Math.Abs(a.Alpha - b.Alpha), 0, 1);
            if (a.Alpha < 20) continue;
            Assert.InRange(Math.Abs(a.Red - b.Red), 0, 4);
            Assert.InRange(Math.Abs(a.Green - b.Green), 0, 4);
            Assert.InRange(Math.Abs(a.Blue - b.Blue), 0, 4);
        }
        using var masked = SKBitmap.Decode(images[2].ToByteArray(Magick.MagickFormat.Png));
        Assert.InRange(masked.GetPixel(2, 2).Alpha, 63, 65);
    }

    [Fact]
    public void Adobe_RGB_pixels_are_converted_to_sRGB_with_their_alpha_preserved()
    {
        Assert.NotNull(BundledImageMagick.TryLoad());
        using var image = new Magick.MagickImage(new Magick.MagickColor("#80B432"), 8, 6);
        image.Depth = 8; image.SetProfile(Magick.ColorProfile.AdobeRGB1998);
        var data = image.ToByteArray(Magick.MagickFormat.Psd);
        var imported = PsdImport.Load(data);
        Assert.Contains(imported.Conversions, n => n.Message.Contains("color profile"));
        image.TransformColorSpace(Magick.ColorProfile.SRGB);
        using var expected = SKBitmap.Decode(image.ToByteArray(Magick.MagickFormat.Png));
        var actual = imported.Layers[0].Pixels!.GetPixel(2, 2); var color = expected.GetPixel(2, 2);
        Assert.InRange(Math.Abs(actual.Red - color.Red), 0, 3);
        Assert.InRange(Math.Abs(actual.Green - color.Green), 0, 3);
        Assert.InRange(Math.Abs(actual.Blue - color.Blue), 0, 3);
        Assert.Equal(color.Alpha, actual.Alpha);
        imported.Discard();
    }

    [AvaloniaFact]
    public async Task PSD_saving_takes_an_immutable_snapshot_and_later_edits_remain_unsaved()
    {
        var window = new MainWindow { Width = 1000, Height = 700 }; window.Show();
        var session = EditorSession.NewCanvas(1200, 900, SKColors.White); window.AddSession(session);
        var folder = Directory.CreateTempSubdirectory("psd-snapshot-").FullName;
        var path = Path.Combine(folder, "snapshot.psd");
        try
        {
            session.Fill(SKColors.Red);
            var save = window.SaveTo(session, path);
            Assert.True(window.IsSaving(session));
            session.Fill(SKColors.Blue);
            await Pump(save); Assert.Null(await save);
            Assert.True(session.IsModified);
            var imported = PsdImport.Load(path);
            Assert.Equal(SKColors.Red, imported.Layers[0].Pixels!.GetPixel(5, 5)); imported.Discard();
            var again = window.SaveTo(session, path); await Pump(again); Assert.Null(await again);
            Assert.False(session.IsModified);
            imported = PsdImport.Load(path);
            Assert.Equal(SKColors.Blue, imported.Layers[0].Pixels!.GetPixel(5, 5)); imported.Discard();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void RGB_16_bit_layers_created_by_ImageMagick_open_with_a_precision_conversion_report()
    {
        Assert.NotNull(BundledImageMagick.TryLoad());
        using var images = new Magick.MagickImageCollection();
        images.Add(new Magick.MagickImage(Magick.MagickColors.Red, 12, 8));
        images.Add(new Magick.MagickImage(Magick.MagickColors.Red, 12, 8));
        images.Add(new Magick.MagickImage(Magick.MagickColors.Blue, 4, 3));
        foreach (var image in images) { image.Depth = 16; image.ColorSpace = Magick.ColorSpace.sRGB; }
        images[1].SetAttribute("label", "Background");
        images[2].SetAttribute("label", "Blue"); images[2].Page = new Magick.MagickGeometry(3, 2, 4, 3);
        using var stream = new MemoryStream(); images.Write(stream, Magick.MagickFormat.Psd);
        var bytes = stream.ToArray();
        Assert.Equal(16, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22)));
        var imported = PsdImport.Load(bytes);
        Assert.Equal(2, imported.Layers.Count);
        Assert.Equal(["Background", "Blue"], imported.Layers.Select(l => l.Name));
        Assert.Equal(SKColors.Blue, imported.Layers[1].Pixels!.GetPixel(1, 1));
        Assert.Equal((3d, 2d), (imported.Layers[1].Transform.X, imported.Layers[1].Transform.Y));
        Assert.Contains(imported.Conversions, n => n.Message.Contains("16-bit"));
        imported.Discard();
    }

    [AvaloniaFact]
    public async Task Complex_photoshop_features_can_open_as_the_authored_merged_appearance()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        var writer = new Composa.Core.Tests.PsdWriter { Width = 24, Height = 16, Composite = Solid(24, 16, SKColors.Coral) };
        writer.Layers.Add(new Composa.Core.Tests.PsdWriterLayer { Name = "Effect layer", Image = Solid(24, 16, SKColors.Blue) }.With("lfx2", new byte[16]));
        var folder = Directory.CreateTempSubdirectory("psd-appearance-").FullName;
        var path = Path.Combine(folder, "complex.psd");
        try
        {
            File.WriteAllBytes(path, writer.Build());
            var open = window.OpenPath(path);
            for (var i = 0; i < 400 && window.OwnedWindows.Count == 0; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            var dialog = Assert.Single(window.OwnedWindows);
            dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            Screenshots.Save(dialog, "48-psd-merged-import");
            dialog.Close(true); await Pump(open);
            var session = (await open)!;
            Assert.Single(session.Document.Layers);
            Assert.Equal(SKColors.Coral, session.Document.Layers[0].Pixels!.GetPixel(3, 3));
            Assert.Null(session.FilePath); // A later Ctrl+S cannot silently overwrite the original layered PSD.
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task The_window_saves_a_real_psd_in_the_background_and_it_reopens_with_its_layers()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        var session = EditorSession.NewCanvas(60, 40, SKColors.Wheat); window.AddSession(session);
        session.AddImageLayer("Accent", Solid(12, 10, SKColors.Coral));
        var folder = Directory.CreateTempSubdirectory("psd-window-").FullName;
        var path = Path.Combine(folder, "work.psd");
        try
        {
            var save = window.SaveTo(session, path); await Pump(save); Assert.Null(await save);
            Assert.False(window.IsSaving(session)); Assert.False(session.IsModified); Assert.Equal(path, session.FilePath);
            Assert.True(PsdImport.IsPsd(path));
            var second = new MainWindow { Width = 1280, Height = 800 }; second.Show();
            var open = second.OpenPath(path); await Pump(open);
            Assert.Equal(2, (await open)!.Document.Layers.Count);
            Assert.Equal(path, (await open)!.FilePath); Assert.False((await open)!.IsModified);
            Assert.Empty(second.OwnedWindows);
            var menus = window.GetVisualDescendants().OfType<Menu>().Single().Items.OfType<MenuItem>().SelectMany(m => m.Items.OfType<MenuItem>()).Select(m => m.Header?.ToString()).ToArray();
            Assert.Contains("Save as PSD…", menus);
            Screenshots.Save(window, "46-psd-saved");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [AvaloniaFact]
    public async Task Export_conversions_use_the_existing_dialog_style_and_cancel_writes_nothing()
    {
        var window = new MainWindow { Width = 1280, Height = 800 }; window.Show();
        var document = new Document(24, 16);
        var layer = Layer.Raster("Shape", Solid(8, 8, SKColors.Red)); layer.Shape = new ShapeStyle(ShapeKind.Rectangle, 0xFFFF0000, 0);
        layer.Transform = layer.Transform with { Distort = [0, 0, 0, 0, -1, -1, 0, 0] };
        document.Layers.Add(layer); window.AddSession(new EditorSession(document));
        var confirm = PsdConversionDialog.Confirm(window, "design.psd", PsdExport.Conversions(document), exporting: true);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(window.OwnedWindows);
        var text = string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains(".cmps", text); Assert.Contains("shape", text);
        Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "Save PSD");
        Screenshots.Save(dialog, "47-psd-save-conversions");
        dialog.Close(false); Assert.False(await confirm);
        Assert.NotNull(layer.Shape);
    }
}
