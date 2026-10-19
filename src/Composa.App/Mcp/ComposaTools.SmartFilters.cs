using System.ComponentModel;
using Composa.Filters;
using Composa.Model;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Composa.App.Mcp;

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "smart_filter")]
    [Description("Non-destructive filters. action: list, add, change, up, down, remove, mask_add, mask_hide_all, mask_delete, mask_enable, mask_disable. Paint the stack mask with paint_stroke(s) target filterMask. add supports GaussianBlur, MotionBlur, AddNoise and Sharpen; radius 0–250 px, amount 0–100, angle -180–180. change adjusts opacity, blend or enabled. filter is an id from list. Order is evaluation order, bottom of the Layers panel first.")]
    public Task<string> SmartFilter(string layer, string action = "list", string? filter = null, string? kind = null,
        double radius = 2, double amount = 10, double angle = 0, uint seed = 1, bool? enabled = null, double? opacity = null, string? blend = null, int? document = null) => OnUi(() =>
    {
        var operation = action.ToLowerInvariant(); var session = operation == "list" ? Session(document) : Editable(document); var target = Find(session, layer);
        if (operation != "list" && session.PixelsLocked(target)) throw new McpException("Unlock the layer before editing its smart filters.");
        if (operation == "add")
        {
            if (!Enum.TryParse<FilterKind>(kind, true, out var type) || type is not (FilterKind.GaussianBlur or FilterKind.MotionBlur or FilterKind.AddNoise or FilterKind.Sharpen))
                throw new McpException("add kind: GaussianBlur, MotionBlur, AddNoise or Sharpen.");
            if (!double.IsFinite(radius) || radius < 0 || radius > 250 || !double.IsFinite(amount) || amount < 0 || amount > 100 || !double.IsFinite(angle) || angle < -180 || angle > 180)
                throw new McpException("radius: 0–250, amount: 0–100, angle: -180–180; values must be finite.");
            if (target.Pixels == null || target.Text != null || target.Shape != null || session.PixelsLocked(target)) throw new McpException("Choose an unlocked raster layer or smart object.");
            if (opacity != null || blend != null || enabled != null) throw new McpException("Add the filter first, then use change with its id for blending or enabled state.");
            session.AddSmartFilter(target, new() { Kind = type, Radius = radius, Amount = amount, Angle = angle, Seed = seed });
        }
        else if (operation.StartsWith("mask_", StringComparison.Ordinal))
        {
            var oldMask = session.EditingMask; var oldFilterMask = session.EditingSmartFilterMask;
            try
            {
                switch (operation)
                {
                    case "mask_add": session.AddSmartFilterMask(target); break;
                    case "mask_hide_all":
                        if (session.Selection != null) throw new McpException("Deselect before mask_hide_all; mask_add uses the selection.");
                        session.AddSmartFilterMask(target, hideAll: true); break;
                    case "mask_delete": session.DeleteSmartFilterMask(target); break;
                    case "mask_enable": session.SetSmartFilterMaskEnabled(target, true); break;
                    case "mask_disable": session.SetSmartFilterMaskEnabled(target, false); break;
                    default: throw new McpException("mask_add, mask_hide_all, mask_delete, mask_enable or mask_disable.");
                }
            }
            finally { session.EditingMask = oldMask; if (oldFilterMask) session.EditingSmartFilterMask = true; }
        }
        else if (operation != "list")
        {
            var matches = target.SmartFilters.Where(f => f.Id.ToString("N").StartsWith(filter ?? "", StringComparison.OrdinalIgnoreCase) || f.Id.ToString().Equals(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (string.IsNullOrWhiteSpace(filter) || matches.Length != 1) throw new McpException("Give one filter id from smart_filter list.");
            var chosen = matches[0];
            switch (operation)
            {
                case "change":
                    if (opacity is { } value && (!double.IsFinite(value) || value < 0 || value > 1)) throw new McpException("opacity is 0–1.");
                    var mode = blend == null ? (BlendMode?)null : ParseBlend(blend);
                    if (mode == BlendMode.PassThrough) throw new McpException("Pass Through applies to groups, not filters.");
                    session.ChangeSmartFilter(target, chosen.Id, enabled: enabled, opacity: opacity, blend: mode); break;
                case "up": session.MoveSmartFilter(target, chosen.Id, 1); break;
                case "down": session.MoveSmartFilter(target, chosen.Id, -1); break;
                case "remove": session.ChangeSmartFilter(target, chosen.Id, remove: true); break;
                default: throw new McpException("action: list, add, change, up, down or remove.");
            }
        }
        return $"Smart filters on \"{target.Name}\" (first applied first); mask={target.SmartFilterMask != null}, maskEnabled={target.SmartFilterMaskEnabled}:\n" + string.Join("\n", target.SmartFilters.Select(f =>
            $"[{f.Id:N}] {f.Settings.Kind}, {(f.Enabled ? "enabled" : "disabled")}, opacity {f.Opacity:0.###}, {f.Blend.DisplayName()}"));
    });
}
