using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Composa.App.Mcp;

public sealed partial class ComposaTools
{
    [McpServerTool(Name = "get_document_state", ReadOnly = true, Idempotent = true)]
    [Description("Read structured canvas, real ruler guides, selection and paged layers in panel top-to-bottom order. Use full ids for edits, follow nextOffset while hasMore. With layer, inspect that one layer by full id or unique name. Shapes/text stay live; a selection is not a layer mask. Text previews flag truncation. Paths returned here can be checked with verify_document.")]
    public Task<string> GetDocumentState(int offset = 0, int count = 6, string? layer = null, int? document = null)
        => OnUi(() => EditorDocumentInspection.Read(Session(document), offset, count, layer));

    [McpServerTool(Name = "verify_document", ReadOnly = true, Idempotent = true)]
    [Description("Check actual editor properties without editing. Each check: layer (full id/unique name, null for document), property from enum, expected scalar. Native kind values: raster, shape, text, group, adjustment, smartObject. Conversions replace inputs: check returned NEW ids, not removed layers. Examples: {layer:null,property:'width',expected:800}; {layer:'Tile',property:'shape.fill',expected:'#FFFF00'}. Default operator equals. Correct and recheck failed checks; a spelling/target error in a check is not a reason to recreate correct objects. Pixel appearance also needs render/sample_color.")]
    public Task<string> VerifyDocument(EditorExpectation[] checks, int? document = null)
        => OnUi(() => EditorDocumentInspection.Verify(Session(document), checks));
}
