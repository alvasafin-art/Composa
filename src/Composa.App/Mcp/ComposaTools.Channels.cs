using System.ComponentModel;
using Composa.Editing;
using Composa.Filters;
using Composa.Model;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Composa.App.Mcp;

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "channels")]
    [Description("RGB components and saved alpha channels. action: list, select (name selects alpha, otherwise rgb), view (visibleRgb and overlay), new (named alpha; optionally fromSelection/revealAll), duplicate, rename (replacement), up/down, delete, load (channel as selection), fill (color), invert, transform (x/y/width/height/rotation of channel contents or the selected fragment). RGB uses all, red, green, blue, or comma-separated components; transform requires a specific component subset or an alpha name. Alpha edits do not change layer pixels; component edits preserve other components and transparency. View never affects exports. paint_stroke(s) address channels explicitly.")]
    public Task<string> Channels(string action = "list", string? name = null, string rgb = "all", string? replacement = null,
        string? visibleRgb = null, bool overlay = false, bool fromSelection = false, bool revealAll = false, string color = "#FFFFFF", int? document = null,
        double? x = null, double? y = null, double? width = null, double? height = null, double? rotation = null) => OnUi(() =>
    {
        var s = action == "list" ? Session(document) : Editable(document);
        switch (action)
        {
            case "list": break;
            case "select": if (name != null) s.EditAlphaChannel(name, overlay); else s.EditRgbChannels(ParseRgbChannels(rgb)); break;
            case "view": s.SetChannelView(ParseRgbChannels(visibleRgb ?? rgb, allowNone: true), overlay); break;
            case "new": s.AddAlphaChannel(name ?? throw new McpException("Give the alpha channel's name."), fromSelection, revealAll); break;
            case "duplicate": s.DuplicateAlphaChannel(RequireAlpha(s, name)); break;
            case "rename": s.RenameAlphaChannel(RequireAlpha(s, name), replacement ?? throw new McpException("Give replacement, the new channel name.")); break;
            case "up": s.MoveAlphaChannel(RequireAlpha(s, name), -1); break;
            case "down": s.MoveAlphaChannel(RequireAlpha(s, name), 1); break;
            case "delete": s.DeleteSelectionChannel(RequireAlpha(s, name)); break;
            case "load": if (name != null) s.LoadSelection(RequireAlpha(s, name)); else s.LoadRgbChannel(ParseRgbChannels(rgb)); break;
            case "fill": case "invert": case "transform":
                var target = CapturePaintTarget(s);
                try
                {
                    if (name != null) s.EditAlphaChannel(RequireAlpha(s, name)); else s.EditRgbChannels(ParseRgbChannels(rgb), changeView: false);
                    if (!s.CanEditPixels) throw new McpException("Choose an unlocked raster layer, or give an alpha channel name.");
                    if (action == "fill") s.Fill(ParseColor(color), "Fill Channel");
                    else if (action == "invert") s.Adjust(new InvertAdjustment());
                    else
                    {
                        var frame = s.ChannelControlTransform;
                        var next = frame with { X = x ?? frame.X, Y = y ?? frame.Y, Width = width ?? frame.Width, Height = height ?? frame.Height, Rotation = rotation ?? frame.Rotation };
                        if (next.Width > DocumentLimits.MaxSide || next.Height > DocumentLimits.MaxSide) throw new McpException($"Channel frame size must not exceed {DocumentLimits.MaxSide} px.");
                        s.SetChannelControlTransform(next);
                    }
                }
                finally { RestorePaintTarget(s, target); }
                break;
            default: throw new McpException("Use list, select, view, new, duplicate, rename, up, down, delete, load, fill, invert or transform.");
        }
        return $"Editing: {s.EditingAlphaChannel ?? (s.IsEditingMask ? "layer mask" : s.EditingRgbChannels.ToString())}. Visible RGB: {s.VisibleRgbChannels}. Alpha overlay: {s.AlphaChannelOverlay}.\nAlpha channels: "
            + (s.Document.AlphaChannels.Count == 0 ? "none" : string.Join(", ", s.Document.AlphaChannels.Keys));
    });

    private static string RequireAlpha(EditorSession session, string? name) => name != null && session.Document.AlphaChannels.ContainsKey(name) ? name
        : throw new McpException("Give the name of an existing alpha channel; channels list shows them.");

    private static RgbChannels ParseRgbChannels(string value, bool allowNone = false) => Enum.TryParse<RgbChannels>(value, true, out var channels)
        && (channels & ~RgbChannels.All) == 0 && (allowNone || channels != RgbChannels.None) ? channels : throw new McpException("RGB channels: all, red, green, blue, or comma-separated components; visibility also accepts none.");

    private sealed record PaintTarget(bool Mask, bool FilterMask, string? Alpha, RgbChannels Rgb, RgbChannels Visible, bool Overlay);
    private static PaintTarget CapturePaintTarget(EditorSession s) => new(s.EditingMask, s.EditingSmartFilterMask, s.EditingAlphaChannel, s.EditingRgbChannels, s.VisibleRgbChannels, s.AlphaChannelOverlay);
    private static void RestorePaintTarget(EditorSession s, PaintTarget target)
    {
        if (target.Alpha != null && s.Document.AlphaChannels.ContainsKey(target.Alpha)) s.EditAlphaChannel(target.Alpha, target.Overlay);
        else
        {
            s.EditRgbChannels(target.Rgb, changeView: false); s.EditingMask = target.Mask;
            if (target.FilterMask) s.EditingSmartFilterMask = true;
        }
        s.SetChannelView(target.Visible, target.Overlay);
    }

    private sealed class PaintTargetScope(EditorSession session, PaintTarget target) : IDisposable
    { public void Dispose() => RestorePaintTarget(session, target); }

    private static IDisposable UsePaintTarget(EditorSession session, string target)
    {
        var previous = CapturePaintTarget(session);
        try { SetPaintTarget(session, target); return new PaintTargetScope(session, previous); }
        catch { RestorePaintTarget(session, previous); throw; }
    }
}
