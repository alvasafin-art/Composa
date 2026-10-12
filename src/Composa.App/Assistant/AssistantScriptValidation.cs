using Acornima;
using Acornima.Ast;
using System.Text.RegularExpressions;

namespace Composa.App.Assistant;

/// <summary>Checks generated artifacts without executing code, opening files or changing a document.</summary>
internal static class AssistantScriptValidation
{
    internal static string? Error(string script, string target)
    {
        try
        {
            var photoshop = target == "photoshop";
            var source = photoshop
                ? Regex.Replace(script, @"(?m)^[ \t]*#(?:targetengine|target|include|includepath)\b[^\r\n]*", "")
                : "(async function(){\n" + script + "\n})();";
            var tree = new Parser(new ParserOptions { EcmaVersion = photoshop ? EcmaVersion.ES3 : EcmaVersion.Latest }).ParseScript(source);
            if (photoshop)
            {
                var nodes = new Stack<Node>(); nodes.Push(tree);
                while (nodes.TryPop(out var node))
                {
                    if (node is NewExpression { Callee: Identifier { Name: "TextItem" or "ArtLayer" or "Document" or "LayerSet" } })
                        return "Photoshop DOM objects are not constructors. Create layers via doc.artLayers.add(), set layer.kind=LayerKind.TEXT, then modify the existing layer.textItem.contents and layer.textItem.size. Never use new TextItem() or assign layer.textItem.";
                    foreach (var child in node.ChildNodes) nodes.Push(child);
                }
            }
            return null;
        }
        catch (ParseErrorException error) { return "Script syntax is invalid for " + target + ": " + error.Message; }
    }
}
