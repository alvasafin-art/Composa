using Composa.Editing;
using Composa.Model;

namespace Composa.App;

/// <summary>Last choices in tool folders, independently of the document's lifetime.</summary>
public sealed record ToolPaletteSettings
{
    public Tool Active { get; init; } = Tool.Move;
    public MarqueeKind Marquee { get; init; }
    public LassoKind Lasso { get; init; }
    public WandMode Wand { get; init; }
    public SmearMode Smear { get; init; }
    public ShapeKind Shape { get; init; }
    public bool Eraser { get; init; }
    public bool SelectionBrush { get; init; }
    public bool ObjectAi { get; init; }
    public bool Bucket { get; init; }
    public Dictionary<string, string> Choices { get; init; } = [];
}
