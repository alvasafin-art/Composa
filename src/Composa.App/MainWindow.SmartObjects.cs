using Composa.Editing;
using Composa.Model;

namespace Composa.App;

public sealed partial class MainWindow
{
    private sealed record EmbeddedTab(EditorSession Parent, SmartObjectSource Source);
    private readonly Dictionary<EditorSession, EmbeddedTab> embeddedTabs = [];

    public EditorSession OpenSmartObject(Layer layer)
    {
        if (session is not { } parent || parent.Document.Find(layer.Id) != layer || layer.SmartObject is not { } source)
            throw new InvalidOperationException("Select a smart object first.");
        if (embeddedTabs.FirstOrDefault(pair => pair.Value.Parent == parent && pair.Value.Source.Id == source.Id).Key is { } open)
        {
            if (!ReferenceEquals(embeddedTabs[open].Source, source))
            {
                if (!open.IsModified && !open.IsEditingText && !embeddedTabs.Values.Any(tab => tab.Parent == open))
                {
                    open.ReloadEmbeddedContents(source.OpenDocument());
                    embeddedTabs[open] = new(parent, source);
                }
                else ShowProblem("The parent smart object changed. Your existing contents are kept; save them as a separate project or close their tabs and reopen to use the parent's version.");
            }
            SetSession(open); return open;
        }
        var contents = new EditorSession(source.OpenDocument()) { SuggestedName = layer.Name + " · Contents" };
        contents.History.BaseName = "Smart Object Contents";
        embeddedTabs.Add(contents, new(parent, source));
        AddSession(contents); return contents;
    }

    internal bool SaveSmartObjectContents(EditorSession contents)
    {
        if (!embeddedTabs.TryGetValue(contents, out var tab)) return false;
        if (!sessions.Contains(tab.Parent)) throw new InvalidOperationException("The parent document is closed. Save these contents as a separate project instead.");
        if (contents.IsEditingText) contents.FinishText();
        if (!contents.IsModified) return true;
        tab.Parent.UpdateSmartObject(tab.Source, contents.Document);
        var replacement = tab.Parent.Document.AllLayers().First(layer => layer.SmartObject?.Id == tab.Source.Id).SmartObject!;
        embeddedTabs[contents] = tab with { Source = replacement };
        contents.MarkEmbeddedSaved(); return true;
    }
}
