using Composa.Model;
using Composa.Rendering;
using Composa.Selections;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    public void SaveSelection(string name)
    {
        FinishText(); name = name.Trim();
        if (document.Selection == null || name.Length is 0 or > 200) return;
        if (document.AlphaChannels.Count >= 64 && !document.AlphaChannels.ContainsKey(name)) throw new InvalidOperationException("The project already has 64 saved selections.");
        if (!document.AlphaChannels.ContainsKey(name) && document.RasterPixels() + (long)document.Width * document.Height > DocumentLimits.DocumentPixelBudget)
            throw new InvalidOperationException("Saved selection exceeds the document raster budget.");
        Apply("Save Selection", () => document.AlphaChannels[name] = document.Selection);
        LayersChanged?.Invoke();
    }
    public void LoadSelection(string name, SelectionMode mode = SelectionMode.Replace)
    {
        if (!document.AlphaChannels.TryGetValue(name, out var mask)) return;
        Select(mask.Width == document.Width && mask.Height == document.Height ? mask : Resample(mask, document.Width, document.Height), mode, "Load Selection");
    }
    public void DeleteSelectionChannel(string name)
    {
        if (!document.AlphaChannels.ContainsKey(name)) return;
        Apply("Delete Selection Channel", () => document.AlphaChannels.Remove(name)); LayersChanged?.Invoke();
    }
}
