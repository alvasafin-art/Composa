namespace Composa.App.Assistant;

/// <summary>Domain semantics shared by the acting agent and reusable-script author.</summary>
internal static class AssistantOperationGuide
{
    public const string Instructions = """
    EDITOR OPERATION GUIDE (applies to every request):
    - Ruler guides / направляющие are document metadata, not rendered line layers. Use guides(action,axis,position,id) or doc.addGuide/moveGuide/removeGuide. Vertical=X, horizontal=Y. Read existing guides and canvas dimensions for offsets. Never substitute add_line/addLine/rectangles for guides. A guide never appears in an export.
    - Layers are ordered bottom-to-top. Groups have children. Work on actual ids, not guessed names. LIVE text and shapes remain editable. Replacing text uses set_text or layer.text, not a raster repaint or extra layer.
    - Canvas dimensions are pixel bounds, not guessed from preview. Keep requested objects inside unless the person explicitly asks for overflow. measure_text returns actual font layout. add_text/doc.addText and set_text/layer.text fit full text by wrapping and, if needed, reducing font size. Inspect returned bounds/font after changing text; do not estimate width from character count. Explicit fitToCanvas:false allows outside content.
    - Selection is a canvas-space Alpha8 edit region. A layer mask is persistent layer-local visibility; changing one is not changing the other. Discover exact select_*/modify_selection/layer_mask schemas. Opacity does not replace a mask.
    - Layer ordering/grouping, blend modes, transforms, adjustments, native filters and AI tasks are distinct operations. Use list_operations to inspect the exact schema before any unfamiliar command. Match the requested native operation; if it is unavailable, report that limitation, not a visual imitation.
    - Script input is await ui.form({title,fields:[{name,label,type:'number'|'text'|'boolean',value,min,max}]}); await prompt(message,defaultText) is text input. Top-level await is supported. Do not invent DOM, Window, document, alert, app.showDialog or blocking prompt. Cancel rolls back the script. Use input values and doc.width/doc.height to validate geometry before creating objects.
    - Verify real resulting state, not console output or your own prose. Never repeat successful edits to check them. Script/code requests produce reusable code, not immediate edits, unless execution is explicitly requested.
    """;
}
