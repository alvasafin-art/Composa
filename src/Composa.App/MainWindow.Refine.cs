using Avalonia.Controls;
using Avalonia.Media;
using Composa.App.Dialogs;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task RefineSelection()
    {
        var target = session!;
        if (target.Selection is not { } original) return;
        var revision = target.Revision;
        using var source = Composa.Rendering.Pixels.Clone(target.Composite());
        var scale = Math.Min(1, 900.0 / Math.Max(source.Width, source.Height));
        using var small = Composa.Rendering.Pixels.NewColor(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
        using (var canvas = new SKCanvas(small)) canvas.DrawBitmap(source, new SKRect(0, 0, small.Width, small.Height));
        using var smallMask = Composa.Rendering.Pixels.NewMask(small.Width, small.Height);
        using (var canvas = new SKCanvas(smallMask)) canvas.DrawBitmap(original, new SKRect(0, 0, small.Width, small.Height));
        var settings = new MaskRefinementSettings(); var background = "Gray"; var output = "Selection"; var decontaminate = false;
        var image = new Image { Width = 560, Height = 380, Stretch = Stretch.Uniform };
        Avalonia.Media.Imaging.Bitmap? displayed = null;
        var outputMenu = Ui.Combo(new[] { "Selection", "Layer mask", "New layer" }, output, o => o, o => output = o, 230);
        void Refresh()
        {
            using var mask = MaskRefinement.Refine(smallMask, small, settings with { Smooth = settings.Smooth * scale, Feather = settings.Feather * scale, Shift = (int)(settings.Shift * scale) });
            using var cutout = MaskRefinement.Cutout(small, mask, decontaminate);
            using var preview = Composa.Rendering.Pixels.NewColor(small.Width, small.Height);
            using (var canvas = new SKCanvas(preview))
            {
                canvas.Clear(background switch { "Black" => SKColors.Black, "White" => SKColors.White, _ => new SKColor(100, 100, 100) });
                if (background == "Mask") { using var white = new SKPaint { Color = SKColors.White }; canvas.DrawBitmap(mask, 0, 0, white); }
                else canvas.DrawBitmap(cutout, 0, 0);
            }
            using var encoded = SKImage.FromBitmap(preview); using var png = encoded.Encode(SKEncodedImageFormat.Png, 100); using var stream = png.AsStream();
            var next = new Avalonia.Media.Imaging.Bitmap(stream); image.Source = next; displayed?.Dispose(); displayed = next;
        }
        var fields = Ui.Column(10,
            Ui.Combo(new[] { "Gray", "Black", "White", "Mask" }, background, b => b, b => { background = b; Refresh(); }, 150),
            Ui.SliderField("Smooth", 0, 0, 20, v => { settings = settings with { Smooth = v }; Refresh(); }, width: 230),
            Ui.SliderField("Feather", 0, 0, 100, v => { settings = settings with { Feather = v }; Refresh(); }, width: 230),
            Ui.SliderField("Contrast", 0, 0, 100, v => { settings = settings with { Contrast = v }; Refresh(); }, width: 230),
            Ui.SliderField("Shift edge", 0, -100, 100, v => { settings = settings with { Shift = (int)v }; Refresh(); }, width: 230),
            Ui.Check("Refine with image edges", true, v => { settings = settings with { EdgeAware = v }; Refresh(); }),
            Ui.Check("Decontaminate edge colors", false, v => { decontaminate = v; if (v) outputMenu.SelectedItem = "New layer"; Refresh(); }),
            outputMenu);
        Refresh();
        var dialog = new DialogWindow("Select and Mask", Ui.Row(18, image, fields));
        var accepted = await dialog.Ask(this); displayed?.Dispose();
        if (!accepted || target.Revision != revision) return;
        if (output == "Layer mask" && !decontaminate && (target.ActiveLayer?.Pixels == null || target.PixelsLocked(target.ActiveLayer)))
        { ShowProblem("Select an unlocked pixel layer to create a layer mask."); return; }
        using var result = await ProgressWindow.Run(this, "Refining mask…", c => Task.Run(() => MaskRefinement.Refine(original, source, settings, c), c));
        if (result == null || target.Revision != revision) return;
        if (output == "New layer" || decontaminate) target.AddImageLayer("Refined cutout", MaskRefinement.Cutout(source, result, decontaminate), fit: false);
        else if (output == "Layer mask") target.RefinedSelectionToMask(result);
        else target.Select(result, Composa.Selections.SelectionMode.Replace, "Select and Mask");
    }
}
