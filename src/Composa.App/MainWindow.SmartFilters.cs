using Composa.App.Dialogs;
using Composa.Filters;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private async Task SmartFilter(FilterKind kind, Layer? chosen = null, SmartFilter? existing = null)
    {
        var target = session!; var layer = chosen ?? target.ActiveLayer;
        if (layer?.Pixels == null || target.PixelsLocked(layer)) return;
        var old = layer.SmartFilters; var id = existing?.Id ?? Guid.NewGuid();
        var initial = existing?.Settings ?? new FilterSettings { Kind = kind, Seed = (uint)Random.Shared.Next() };
        target.Begin(existing == null ? "Add Smart Filter" : "Change Smart Filter");
        void Preview(FilterSettings settings)
        {
            var filters = existing == null ? old.Append(new SmartFilter(id, settings)).ToArray() : old.Select(f => f.Id == id ? f with { Settings = settings } : f).ToArray();
            Busy(() => target.PreviewSmartFilters(layer, filters));
        }
        try
        {
            FilterSettings? result;
            if (kind == FilterKind.CameraRaw)
            {
                var grade = await CameraRawDialog.Show(this, initial.CameraRaw, layer.FilterSource ?? layer.Pixels,
                    g => Preview(initial with { CameraRaw = g }), () => layer.Pixels);
                result = grade == null ? null : initial with { CameraRaw = grade };
            }
            else result = await AdjustmentDialogs.EditFilter(this, initial, Preview);
            if (result == null || result.IsIdentity) target.Cancel();
            else { Preview(result); target.Commit(); }
        }
        catch { target.Cancel(); throw; }
    }
}
