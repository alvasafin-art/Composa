using System.ComponentModel;
using System.Text.Json;
using Composa.Model;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Composa.App.Mcp;

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "guides")]
    [Description("REAL ruler/snap guides, not painted lines or layers. action: list, add, move, remove, clear, show, hide, lock, unlock. Vertical position is X; horizontal is Y, in canvas pixels. add needs axis/position; move needs id/position; remove needs id. Guides do not appear in exports.")]
    public Task<string> Guides(string action = "list", string? axis = null, double? position = null, string? id = null, int? document = null) => OnUi(() =>
    {
        var s = action == "list" ? Session(document) : Editable(document);
        switch (action)
        {
            case "list": break;
            case "show": s.View = s.View with { ShowGuides = true, ShowRulers = true }; break;
            case "hide": s.View = s.View with { ShowGuides = false }; break;
            case "lock": s.View = s.View with { LockGuides = true }; break;
            case "unlock": s.View = s.View with { LockGuides = false }; break;
            case "clear": s.ClearGuides(); break;
            case "add":
                if (!s.CanEditGuides) throw new McpException("Guides are locked; explicitly unlock them first.");
                if (!Enum.TryParse<GuideAxis>(axis, true, out var direction) || !Enum.IsDefined(direction)) throw new McpException("axis: vertical (X) or horizontal (Y).");
                var at = GuidePosition(position);
                if (!s.Guides.Any(guide => guide.Axis == direction && guide.Position == at) && s.AddGuide(direction, at) == null)
                    throw new McpException("The document's guide limit was reached.");
                s.View = s.View with { ShowGuides = true, ShowRulers = true }; break;
            case "move":
                if (!s.CanEditGuides) throw new McpException("Guides are locked; explicitly unlock them first.");
                s.MoveGuide(GuideId(s.Guides, id), GuidePosition(position)); break;
            case "remove":
                if (!s.CanEditGuides) throw new McpException("Guides are locked; explicitly unlock them first.");
                s.RemoveGuide(GuideId(s.Guides, id)); break;
            default: throw new McpException("action: list, add, move, remove, clear, show, hide, lock, unlock.");
        }
        window.Canvas.InvalidateVisual();
        return JsonSerializer.Serialize(new { showGuides = s.View.ShowGuides, showRulers = s.View.ShowRulers, locked = s.View.LockGuides,
            guides = s.Guides.Select(guide => new { id = guide.Id, axis = guide.Axis.ToString().ToLowerInvariant(), position = guide.Position }) });
    });

    private static double GuidePosition(double? position) => position is { } value && double.IsFinite(value) && Math.Abs(value) <= DocumentLimits.MaxSide
        ? value : throw new McpException($"Use a finite guide position within ±{DocumentLimits.MaxSide}px.");
    private static Guid GuideId(IReadOnlyList<Guide> guides, string? id) => Guid.TryParse(id, out var value) && guides.Any(guide => guide.Id == value)
        ? value : throw new McpException("Use the full id of an existing guide from get_document/guides.");
}
