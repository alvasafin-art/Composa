using Avalonia.Controls;

namespace Composa.App.AI;

/// <summary>A view over the window's shared reference list, not a second owner of its bitmaps.</summary>
public sealed class AiReferenceEditor(Control view, Func<int> count, Func<TextBox?, Task> paste)
{
    public Control View { get; } = view;
    public int Count => count();
    public Window? Owner { get; set; }
    public Task Paste(TextBox? prompt) => paste(prompt);
    public event Action? Changed;
    internal void NotifyChanged() => Changed?.Invoke();
}
