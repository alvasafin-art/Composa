using Avalonia.Controls;
using Composa.AI;
using Composa.App.Dialogs;
using Composa.Selections;
using Composa.Vision;
using SkiaSharp;
using SelectionMode = Composa.Selections.SelectionMode;

namespace Composa.App;

public sealed partial class MainWindow
{
    private bool localSelectionBusy;
    private SubjectDetect LocalDetect => settings.ObjectSelectionModel switch
    {
        ObjectSelectionSource.Person => SubjectDetect.Person,
        ObjectSelectionSource.PlainBackdrop => SubjectDetect.Backdrop,
        _ => SubjectDetect.Any
    };

    private Control ObjectSelectionModelMenu()
    {
        var combo = Ui.Combo(Enum.GetValues<ObjectSelectionSource>(), settings.ObjectSelectionModel,
            source => source switch
            {
                ObjectSelectionSource.AnySubject => "U²-Net lite · Any subject",
                ObjectSelectionSource.Person => "MODNet · Person",
                ObjectSelectionSource.PlainBackdrop => "Plain backdrop",
                _ => "ComfyUI"
            }, source =>
            {
                settings.ObjectSelectionModel = source;
                if (session != null) session.Detect = LocalDetect;
                settings.Save();
            }, 215);
        combo.Name = "ObjectSelectionModel";
        ToolTip.SetTip(combo, "U²-Net lite and MODNet run on this computer. ComfyUI uses the workflow assigned in AI settings.");
        return Ui.Row(6, Ui.Label("Model", Palette.Secondary), combo);
    }

    internal async Task RunObjectSelection(SKRectI? region, SelectionMode mode, SKPointI? point = null)
    {
        var target = session;
        if (target == null || localSelectionBusy || canvas.IsDragging || target.IsInteracting) return;
        if (settings.ObjectSelectionModel == ObjectSelectionSource.PlainBackdrop && region == null)
        {
            if (point is { } p) target.SelectObject(p.X, p.Y, mode);
            else if (!target.SelectSubject(mode)) ShowNote("No subject stands out from the plain backdrop.");
            return;
        }
        if (settings.ObjectSelectionModel == ObjectSelectionSource.ComfyUI)
        {
            if (point is { } p)
            {
                // A ComfyUI workflow consumes a search image, so a point uses the whole picture.
                if (p.X < 0 || p.Y < 0 || p.X >= target.Document.Width || p.Y >= target.Document.Height) return;
                region = target.Document.Bounds;
            }
            await RunAi(AiTaskKind.ObjectSelection, useInlinePrompt: true, selectionRegion: region ?? target.Document.Bounds, selectionOperation: mode);
            return;
        }
        target.Detect = LocalDetect;
        var detect = target.Detect;
        var fallback = SubjectFinder.FallbackReason(detect);
        localSelectionBusy = true;
        try
        {
            var found = await ProgressWindow.Run(this, "Selecting object…", async cancellation =>
            {
                if (region is { } box) { await target.SelectObjectInBoxAsync(box, mode, cancellation); return true; }
                if (point is { } p) { await target.SelectObjectAsync(p.X, p.Y, mode, cancellation); return true; }
                return await target.SelectSubjectAsync(mode, cancellation);
            });
            if (session != target) return;
            if (fallback != null) ShowNote(fallback);
            else if (!found) ShowNote("No subject found, the operation was cancelled, or the picture changed while selecting.");
        }
        catch (Exception error) { ShowProblem("Object selection failed: " + error.Message); }
        finally { localSelectionBusy = false; }
    }
}
