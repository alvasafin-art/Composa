using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.IO.Psd;

public static partial class PsdExport
{
    private sealed class NativePathFrame
    {
        public SKRect Bounds { get; }
        public SKPoint Origin => new(Bounds.Left, Bounds.Top);
        public SKRect LocalBounds => new(0, 0, Bounds.Width, Bounds.Height);
        public Dictionary<string, object> Data { get; }

        public NativePathFrame(TextPath path)
        {
            using var outline = path.Outline.Build(1, 1);
            Bounds = outline.TightBounds;
            var nodes = path.Outline.Nodes;
            int count = path.Outline.Closed ? nodes.Length : nodes.Length - 1;
            var points = new List<object>(count * 8);
            for (int i = 0; i < count; i++)
            {
                var a = nodes[i];
                var b = nodes[(i + 1) % nodes.Length];
                points.AddRange([a.X, a.Y, a.OutX, a.OutY, b.InX, b.InY, b.X, b.Y]);
            }

            Data = Dict(("1", Dict(("0", points))), ("2", Dict(
                // These coefficients are real numbers in the text engine syntax.
                // Photoshop reads integer coefficients as 16.16 fixed-point values when it recomposes the text.
                ("0", 2), ("2", new object[] { 1.0, 0.0, 0.0, 1.0, -(double)Bounds.Left, -(double)Bounds.Top }),
                ("6", new object[] { path.Start, path.End ?? (path.Outline.Closed ? path.Start + count : count) }),
                ("11", Dict(("3", 0), ("5", 0))))));
        }
    }

    // Txt2 is the document-wide text engine table. Each TySh's TextIndex addresses a story;
    // the story's composer references the frame that owns the editable Bézier geometry.
    private sealed class NativeTypeTable
    {
        private readonly Dictionary<Layer, int> indices = new();
        private readonly List<object> frames = [], stories = [], fonts = [];
        private readonly Dictionary<string, int> fontIndices = new(StringComparer.Ordinal);
        private readonly bool hasPath;

        public NativeTypeTable(List<Record> records, Document document)
        {
            if (!records.Any(r => r.Layer.Text?.Path != null)) return;
            var layers = records.Where(r => r.Section == 0 && !r.Appearance && r.Layer.Text != null && HasNativeContent(r.Layer, document))
                .Select(r => r.Layer).ToArray();
            hasPath = layers.Any(layer => layer.Text!.Path != null);
            if (!hasPath) return;
            FontId("AdobeInvisFont");
            foreach (var layer in layers)
            {
                var style = layer.Text!.Clamped();
                var index = stories.Count;
                indices.Add(layer, index);
                var path = style.Path == null ? null : new NativePathFrame(style.Path);
                var layout = new TextLayout(style);
                var shorthand = path == null ? SimpleFrame(style, layout) : path.Data;
                frames.Add(Dict(("0", shorthand)));
                stories.Add(Story(style, index, path));
            }
        }

        public int Index(Layer layer) => indices.GetValueOrDefault(layer);

        private int FontId(string name)
        {
            if (fontIndices.TryGetValue(name, out int id)) return id;
            fontIndices.Add(name, id = fonts.Count);
            fonts.Add(Dict(("0", Dict(("99", new EngineName("CoolTypeFont")), ("0", Dict(("0", name), ("2", 0)))))));
            return id;
        }

        public void Write(Output output)
        {
            if (!hasPath) return;
            var black = Paint(0xff000000);
            var normalCharacter = Dict(("0", 1), ("1", 12.0), ("4", true), ("6", 1.0), ("7", 1.0), ("11", 1),
                ("18", true), ("35", 2), ("37", 2), ("53", black), ("54", black), ("56", true), ("58", true), ("70", 0));
            var normalParagraph = Dict(("0", 0), ("7", 1.2), ("9", false), ("17", new object[] { .8, 1.0, 1.33 }),
                ("18", new object[] { 0.0, 0.0, 0.0 }), ("19", new object[] { 1.0, 1.0, 1.0 }), ("33", 0), ("35", 1));
            object NamedStyle(string name, string key, object value) => Dict(("0", Dict(("0", name), (key, value))));
            var resources = Dict(("1", Dict(("0", fonts))),
                ("5", Dict(("0", new[] { NamedStyle("Normal RGB", "6", normalCharacter), NamedStyle("None", "6", Dict()) }))),
                ("6", Dict(("0", new[] { NamedStyle("Normal RGB", "5", normalParagraph), NamedStyle("Basic Paragraph", "5", normalParagraph) }))),
                ("8", Dict(("0", frames))));
            object Separator(string value, string kind) => Dict(("0", value), ("1", kind));
            var engine = Dict(("0", Dict(("0", 0), ("1", new[] { Separator(" ", "1"), Separator("\r", "6"),
                    Separator("\t", "0"), Separator("\u2029", "5"), Separator("\u0003", "5"),
                    Separator("\u3000", "1"), Separator("\u00ad", "3") }))),
                ("1", 0), ("2", 0), ("3", .583), ("4", .333), ("5", .583), ("6", .333), ("7", .7), ("8", true),
                ("9", new[] { Dict(("0", 0), ("1", "“"), ("2", "”"), ("3", "‘"), ("4", "’")) }),
                ("15", Dict(("0", ""))), ("16", true), ("17", 0));
            var data = Dict(("98", Dict(("0", 13))), ("0", resources), ("1", Dict(("0", engine), ("1", stories))));
            output.Additional("Txt2", () => output.Bytes(EncodeEngine(data, bare: true)));
        }

        private static Dictionary<string, object> SimpleFrame(TextStyle style, TextLayout layout)
        {
            if (!style.IsBox) return Dict(("2", Dict(("6", new object[] { -1, -1 }), ("11", Dict(("4", -1), ("18", -1))))));
            double w = layout.Width - 2 * TextLayout.Padding, h = layout.Height - 2 * TextLayout.Padding;
            return Dict(("1", Dict(("0", new object[] {
                0.0, 0.0, 0.0, 0.0, w, 0.0, w, 0.0, w, 0.0, w, 0.0, w, h, w, h,
                w, h, w, h, 0.0, h, 0.0, h, 0.0, h, 0.0, h, 0.0, 0.0, 0.0, 0.0
            }))), ("2", Dict(("0", 1), ("6", new object[] { -2, -2 }), ("11", Dict(("4", -1), ("18", -1))))));
        }

        private Dictionary<string, object> Story(TextStyle style, int index, NativePathFrame? path)
        {
            var content = PhotoshopText(style) + "\r";
            var segments = Segments(style);
            var characters = new List<object>(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                var r = segments[i];
                var name = PsdFonts.PostScriptName(TextLayout.TypefaceFor(r.Face));
                var intrinsic = PsdFonts.IntrinsicFace(TextLayout.TypefaceFor(r.Face));
                var f = r.Format;
                var paint = Paint(r.Color);
                var data = Dict(("0", FontId(name)), ("1", r.Size),
                    ("2", r.Face.FontStyle == null && r.Face.Bold && !intrinsic.Bold),
                    ("3", r.Face.FontStyle == null && r.Face.Italic && !intrinsic.Italic),
                    ("4", f.Leading == 0), ("5", f.Type.PercentLeading ? r.Size * f.Leading / 100 : f.Leading), ("6", 1.0), ("7", 1.0),
                    ("8", (int)Math.Round(f.Type.PercentTracking ? f.Tracking * 10 : f.Tracking * 1000 / r.Size)),
                    ("11", 1), ("12", f.Type.Case == TextLetterCase.Upper ? 2 : f.Type.Case == TextLetterCase.SmallCaps ? 1 : 0),
                    ("13", (int)f.Type.FigurePosition), ("15", f.Type.Underline ? 1 : 0),
                    ("16", f.Type.Strike ? 1 : 0), ("18", true), ("20", true),
                    ("35", 1), ("37", 2), ("53", paint), ("54", paint), ("70", 0), ("91", 0));
                characters.Add(Run(Dict(("0", ""), ("5", 0), ("6", data)), r.Length + (i == segments.Count - 1 ? 1 : 0)));
            }

            var paragraphs = new List<object>();
            for (int i = 0, start = 0; i < content.Length; i++)
            {
                if (content[i] != '\r') continue;
                var format = style.FormatAt(start);
                var paragraph = Dict(("0", format.Alignment switch { TextAlignment.Right => 1, TextAlignment.Center => 2, TextAlignment.Justify => 3, _ => 0 }),
                    ("1", format.Type.Indent), ("5", format.Type.ParagraphSpacing), ("9", false), ("33", 0), ("35", 1));
                paragraphs.Add(Run(Dict(("0", ""), ("5", paragraph), ("6", 1)), i - start + 1));
                start = i + 1;
            }

            var renderedFrame = Dict(("99", new EngineName("F")), ("10", index), ("6", Array.Empty<object>()));
            if (path != null)
            {
                renderedFrame.Add("1", new object[] { 0.0, 0.0, (double)path.LocalBounds.Right, (double)path.LocalBounds.Bottom });
                renderedFrame.Add("5", 2);
            }
            return Dict(("0", Dict(("0", content), ("5", Dict(("0", paragraphs))), ("6", Dict(("0", characters))), ("10", Dict(("0", 4), ("2", true))))),
                ("1", Dict(("0", new object[] { Dict(("0", index)) }),
                    ("2", new object[] { Dict(("99", new EngineName("PC")), ("5", 0), ("6", new object[] { renderedFrame })) }),
                    ("4", Dict(("0", 7), ("1", 10), ("2", 0), ("3", "Photoshop"))), ("5", 0))));
        }

        private static Dictionary<string, object> Paint(uint color) => Dict(("99", new EngineName("SimplePaint")),
            ("0", Dict(("0", 1), ("1", new object[] { 1.0, (color >> 16 & 255) / 255.0, (color >> 8 & 255) / 255.0, (color & 255) / 255.0 }))));

        private static Dictionary<string, object> Run(Dictionary<string, object> style, int length) =>
            Dict(("0", Dict(("0", style))), ("1", length));
    }
}
