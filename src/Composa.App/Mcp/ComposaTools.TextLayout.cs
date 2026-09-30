using System.ComponentModel;
using System.Text.Json;
using Composa.Model;
using Composa.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Composa.App.Mcp;

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "measure_text", ReadOnly = true, Idempotent = true)]
    [Description("Measures actual renderer font layout without creating a layer. Returns width/height, lineCount and overflow. Optional boxWidth/boxHeight wrap a paragraph. Use before placing text; never estimate its width from character count.")]
    public Task<string> MeasureText(string text, double size = 72, string? font = null, bool bold = false, bool italic = false,
        double? boxWidth = null, double? boxHeight = null, int? document = null) => OnUi(() =>
    {
        var s = Session(document);
        if (text.Length > TextStyle.MaxLength || !double.IsFinite(size) || size < 1 || size > 2000) throw new McpException("Invalid text length or font size.");
        var layout = new TextLayout(s.TextDefaults with { Text = text, Size = size, FontFamily = font ?? s.TextDefaults.FontFamily,
            Bold = bold, Italic = italic, BoxWidth = boxWidth, BoxHeight = boxHeight });
        return JsonSerializer.Serialize(new { width = layout.Width, height = layout.Height, lineCount = layout.Lines.Count, overflow = layout.Overflows,
            padding = TextLayout.Padding, canvasWidth = s.Document.Width, canvasHeight = s.Document.Height });
    });
}
