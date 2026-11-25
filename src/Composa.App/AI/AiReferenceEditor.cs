using Avalonia.Controls;

namespace Composa.App.AI;

/// <summary>A view over the window's shared reference list, not a second owner of its bitmaps.</summary>
public sealed class AiReferenceEditor
{
    private readonly Func<int> count;
    private readonly Func<TextBox?, Task> paste;
    private readonly Func<Task> pick;
    private readonly Func<IReadOnlyList<AiImageMention>> images;

    internal AiReferenceEditor(Control view, Func<int> count, Func<TextBox?, Task> paste, Func<Task> pick, Func<IReadOnlyList<AiImageMention>> images)
    {
        View = view;
        this.count = count;
        this.paste = paste;
        this.pick = pick;
        this.images = images;
    }

    public Control View { get; }
    public int Count => count();
    public Window? Owner { get; set; }
    public Task Paste(TextBox? prompt) => paste(prompt);
    internal Task Pick() => pick();
    internal IReadOnlyList<AiImageMention> Images => images();
    public event Action? Changed;
    internal event Action<int>? Removed;
    internal void NotifyChanged() => Changed?.Invoke();
    internal void NotifyRemoved(int index) => Removed?.Invoke(index);
}
