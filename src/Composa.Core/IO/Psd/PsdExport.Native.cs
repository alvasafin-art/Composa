using System.Globalization;
using System.Text;
using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.IO.Psd;

public static partial class PsdExport
{
    // These are Photoshop's public additional-info blocks, not Composa metadata. Raster channels remain a
    // compatibility cache; TySh or the vector blocks are what another editor uses when the layer is edited.
    private const int MaxNativeStyleRuns = 2048;

    private static bool HasNativeContent(Layer layer, Document document) => (layer.Text != null || layer.Shape != null) && NativeProblem(layer, document) == null;

    private static string? NativeProblem(Layer layer, Document document)
    {
        if (layer.Pixels == null) return "The live layer has no render cache.";
        if (layer.Effects?.Visible() is { IsEmpty: false }) return "This live layer has effects whose Photoshop settings are not yet supported.";
        var m = layer.Matrix;
        if (!new[] { m.ScaleX, m.ScaleY, m.SkewX, m.SkewY, m.TransX, m.TransY }.All(float.IsFinite)
            || Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY) < 1e-8) return "The live layer has a degenerate transform.";
        if (m.Persp0 != 0 || m.Persp1 != 0 || m.Persp2 != 1) return "Perspective distortion cannot be represented by the native text or shape transform.";
        if (layer.Text is { IsBox: true } boxed && (boxed.BoxWidth <= TextLayout.Padding * 2 || boxed.BoxHeight <= TextLayout.Padding * 2))
            return "The paragraph frame is too small for a portable Photoshop text box.";
        if (layer.Text is { } text && Segments(text).Count > MaxNativeStyleRuns) return "The text has too many separate style runs for a portable Photoshop text block.";
        if (layer.Shape is { } shape)
        {
            var corners = layer.Transform.Corners(layer.Pixels.Width, layer.Pixels.Height);
            if (corners.Any(p => p.X / document.Width < -128 || p.X / document.Width >= 128 || p.Y / document.Height < -128 || p.Y / document.Height >= 128))
                return "The vector contour is too far outside the canvas for Photoshop's fixed-point path coordinates.";
            if (((shape.FillEnabled || shape.Kind == ShapeKind.Line) && (shape.Fill >> 24) != 255) || (shape.Stroke is { } stroke && (stroke >> 24) != 255))
                return "Separate translucent vector fills and strokes cannot be represented exactly.";
            if ((shape.Stroke != null || shape.Kind == ShapeKind.Line) && !Uniform(m)) return "The stretched or sheared vector stroke cannot be represented exactly by Photoshop's stroke width.";
            if (shape.Kind != ShapeKind.Line && shape.Stroke != null && shape.StrokeWidth >= Math.Min(layer.Pixels.Width, layer.Pixels.Height))
                return "The vector stroke covers the entire shape and leaves no portable shape contour.";
        }
        return null;
    }

    private static bool Uniform(SKMatrix m)
    {
        var x = Math.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
        var y = Math.Sqrt(m.SkewX * m.SkewX + m.ScaleY * m.ScaleY);
        return x > 1e-6 && Math.Abs(x - y) <= 1e-5 * Math.Max(x, y)
            && Math.Abs(m.ScaleX * m.SkewX + m.SkewY * m.ScaleY) <= 1e-5 * x * y;
    }

    private static void WriteNative(Output o, Layer layer, Document document)
    {
        if (layer.Text != null) WriteText(o, layer);
        else WriteShape(o, layer, document);
    }

    private sealed record Segment(int Start, int Length, TextFace Face, uint Color);

    private static List<Segment> Segments(TextStyle style)
    {
        var cuts = new SortedSet<int> { 0, style.Text.Length };
        foreach (var r in style.ColorRuns ?? []) { cuts.Add(r.Start); cuts.Add(r.End); }
        foreach (var r in style.FontRuns ?? []) { cuts.Add(r.Start); cuts.Add(r.End); }
        var result = new List<Segment>();
        var points = cuts.ToArray();
        int colorIndex = 0, fontIndex = 0;
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var start = points[i];
            while (style.ColorRuns != null && colorIndex < style.ColorRuns.Count && style.ColorRuns[colorIndex].End <= start) colorIndex++;
            while (style.FontRuns != null && fontIndex < style.FontRuns.Count && style.FontRuns[fontIndex].End <= start) fontIndex++;
            var color = style.ColorRuns != null && colorIndex < style.ColorRuns.Count && style.ColorRuns[colorIndex].Start <= start ? style.ColorRuns[colorIndex].Color : style.Color;
            var face = style.FontRuns != null && fontIndex < style.FontRuns.Count && style.FontRuns[fontIndex].Start <= start ? style.FontRuns[fontIndex].Face : style.Face;
            var length = points[i + 1] - start;
            if (result.Count > 0 && result[^1].Face == face && result[^1].Color == color) result[^1] = result[^1] with { Length = result[^1].Length + length };
            else result.Add(new(start, length, face, color));
        }
        if (result.Count == 0) result.Add(new(0, 0, style.Face, style.Color));
        return result;
    }

    private static void WriteText(Output o, Layer layer)
    {
        var style = layer.Text!.Clamped();
        var layout = new TextLayout(style);
        var first = layout.Lines[0];
        var anchor = style.IsBox ? new SKPoint(TextLayout.Padding, TextLayout.Padding)
            : new SKPoint(first.X + first.VisibleWidth * (style.Alignment == TextAlignment.Right ? 1 : style.Alignment == TextAlignment.Center ? .5f : 0), first.Baseline);
        var m = layer.Matrix;
        var position = m.MapPoint(anchor);
        var frame = style.IsBox ? new SKRect(0, 0, (float)style.BoxWidth!.Value - TextLayout.Padding * 2, (float)style.BoxHeight!.Value - TextLayout.Padding * 2)
            : new SKRect(-anchor.X, -anchor.Y, layout.Width - anchor.X, layout.Height - anchor.Y);
        var glyphs = new SKRect(TextLayout.Padding - anchor.X, TextLayout.Padding - anchor.Y,
            layout.Width - TextLayout.Padding - anchor.X, layout.Height - TextLayout.Padding - anchor.Y);
        var text = new D("TxLr").Text("Txt ", style.Text.Replace('\n', '\r')).Enum("textGridding", "textGridding", "None")
            .Enum("Ornt", "Ornt", "Hrzn").Enum("AntA", "Annt", "AnSm").Long("TextIndex", 0)
            .Obj("bounds", RectDescriptor(frame, "Rctn", "#Pnt")).Obj("boundingBox", RectDescriptor(glyphs, "boundingBox", "#Pnt"))
            .Raw("EngineData", TextEngine(style, frame));
        o.Additional("TySh", () =>
        {
            o.U16(1);
            // Photoshop uses the PostScript affine order a,b,c,d,tx,ty (b is the y component of the x axis).
            foreach (var v in new double[] { m.ScaleX, m.SkewY, m.SkewX, m.ScaleY, position.X, position.Y }) o.F64(v);
            o.U16(50); text.Write(o, versioned: true);
            o.U16(1);
            new D("warp").Enum("warpStyle", "warpStyle", "warpNone").Double("warpValue", 0)
                .Double("warpPerspective", 0).Double("warpPerspectiveOther", 0).Enum("warpRotate", "Ornt", "Hrzn").Write(o, true);
            o.F32(frame.Left); o.F32(frame.Top); o.F32(frame.Right); o.F32(frame.Bottom);
        });
    }

    private static Dictionary<string, object> Dict(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static byte[] TextEngine(TextStyle s, SKRect frame)
    {
        var segments = Segments(s);
        var faces = new List<TextFace> { s.Face };
        foreach (var run in segments) if (!faces.Contains(run.Face)) faces.Add(run.Face);
        object Color(uint color) => Dict(("Type", 1), ("Values", new object[] { 1.0, (color >> 16 & 255) / 255.0, (color >> 8 & 255) / 255.0, (color & 255) / 255.0 }));
        object Sheet(TextFace face, uint color) => Dict(
            ("Font", faces.IndexOf(face) + 1), ("FontSize", s.Size), ("FauxBold", face.FontStyle == null && face.Bold && TextLayout.TypefaceFor(face).FontWeight < (int)SKFontStyleWeight.SemiBold),
            ("FauxItalic", face.FontStyle == null && face.Italic && TextLayout.TypefaceFor(face).FontSlant == SKFontStyleSlant.Upright),
            ("AutoLeading", s.Leading == 0), ("Leading", s.LineHeight), ("HorizontalScale", 1.0), ("VerticalScale", 1.0),
            ("Tracking", s.Tracking * 1000 / s.Size), ("AutoKerning", true), ("Kerning", 0), ("BaselineShift", 0.0),
            ("FontCaps", 0), ("FontBaseline", 0), ("Underline", false), ("Strikethrough", false), ("Ligatures", true), ("DLigatures", false),
            ("BaselineDirection", 2), ("Tsume", 0.0), ("StyleRunAlignment", 2), ("Language", 0), ("NoBreak", false),
            ("FillColor", Color(color)), ("StrokeColor", Color(s.Color)), ("FillFlag", true), ("StrokeFlag", false), ("FillFirst", true),
            ("YUnderline", 1), ("OutlineWidth", 1.0), ("CharacterDirection", 0), ("HindiNumbers", false), ("Kashida", 1), ("DiacriticPos", 2));
        var paragraphProperties = Dict(("Justification", s.Alignment == TextAlignment.Right ? 1 : s.Alignment == TextAlignment.Center ? 2 : 0),
            ("FirstLineIndent", 0.0), ("StartIndent", 0.0), ("EndIndent", 0.0), ("SpaceBefore", 0.0), ("SpaceAfter", 0.0),
            ("AutoHyphenate", false), ("AutoLeading", 1.2), ("LeadingType", 0), ("WordSpacing", new object[] { .8, 1.0, 1.33 }),
            ("LetterSpacing", new object[] { 0.0, 0.0, 0.0 }), ("GlyphSpacing", new object[] { 1.0, 1.0, 1.0 }), ("EveryLineComposer", false));
        var paragraph = Dict(("ParagraphSheet", Dict(("DefaultStyleSheet", 0), ("Properties", paragraphProperties))),
            ("Adjustments", Dict(("Axis", new object[] { 1.0, 0.0, 1.0 }), ("XY", new object[] { 0.0, 0.0 }))));
        var content = s.Text.Replace('\n', '\r') + "\r";
        var paragraphs = new List<object>(); var lengths = new List<object>();
        for (int start = 0, i = 0; i < content.Length; i++) if (content[i] == '\r') { paragraphs.Add(paragraph); lengths.Add(i - start + 1); start = i + 1; }
        var fonts = new List<object> { Dict(("Name", "AdobeInvisFont"), ("Script", 0), ("FontType", 0), ("Synthetic", 0)) };
        foreach (var face in faces) fonts.Add(Dict(("Name", PsdFonts.PostScriptName(TextLayout.TypefaceFor(face))), ("Script", 0), ("FontType", 0), ("Synthetic", 0)));
        var resources = Dict(("KinsokuSet", Array.Empty<object>()), ("MojiKumiSet", Array.Empty<object>()), ("TheNormalStyleSheet", 0), ("TheNormalParagraphSheet", 0),
            ("ParagraphSheetSet", new object[] { Dict(("Name", "Normal RGB"), ("DefaultStyleSheet", 0), ("Properties", paragraphProperties)) }),
            ("StyleSheetSet", new object[] { Dict(("Name", "Normal RGB"), ("StyleSheetData", Sheet(s.Face, s.Color))) }), ("FontSet", fonts),
            ("SuperscriptSize", .583), ("SuperscriptPosition", .333), ("SubscriptSize", .583), ("SubscriptPosition", .333), ("SmallCapSize", .7));
        var photoshop = Dict(("ShapeType", s.IsBox ? 1 : 0));
        photoshop[s.IsBox ? "BoxBounds" : "PointBase"] = s.IsBox ? new object[] { (double)frame.Left, (double)frame.Top, (double)frame.Right, (double)frame.Bottom } : new object[] { 0.0, 0.0 };
        photoshop["Base"] = Dict(("ShapeType", s.IsBox ? 1 : 0), ("TransformPoint0", new object[] { 1.0, 0.0 }), ("TransformPoint1", new object[] { 0.0, 1.0 }), ("TransformPoint2", new object[] { 0.0, 0.0 }));
        var engine = Dict(("EngineDict", Dict(("Editor", Dict(("Text", content))),
            ("ParagraphRun", Dict(("DefaultRunData", paragraph), ("RunArray", paragraphs), ("RunLengthArray", lengths), ("IsJoinable", 1))),
            ("StyleRun", Dict(("DefaultRunData", Dict(("StyleSheet", Dict(("StyleSheetData", Sheet(s.Face, s.Color)))))),
                ("RunArray", segments.Select(r => (object)Dict(("StyleSheet", Dict(("StyleSheetData", Sheet(r.Face, r.Color)))))).ToArray()),
                ("RunLengthArray", segments.Select((r, i) => (object)(r.Length + (i == segments.Count - 1 ? 1 : 0))).ToArray()), ("IsJoinable", 2))),
            ("GridInfo", Dict(("GridIsOn", false), ("ShowGrid", false), ("GridSize", 18.0), ("GridLeading", 22.0),
                ("GridColor", Color(0xFF0000FF)), ("GridLeadingFillColor", Color(0xFF0000FF)), ("AlignLineHeightToGridFlags", false))),
            ("AntiAlias", 3), ("UseFractionalGlyphWidths", true),
            ("Rendered", Dict(("Version", 1), ("Shapes", Dict(("WritingDirection", 0), ("Children", new object[] {
                Dict(("ShapeType", s.IsBox ? 1 : 0), ("Procession", 0), ("Lines", Dict(("WritingDirection", 0), ("Children", Array.Empty<object>()))),
                    ("Cookie", Dict(("Photoshop", photoshop)))) }))))))), ("ResourceDict", resources), ("DocumentResources", resources));
        using var stream = new MemoryStream();
        void Ascii(string v) => stream.Write(Encoding.ASCII.GetBytes(v));
        void Value(object value)
        {
            switch (value)
            {
                case Dictionary<string, object> dictionary:
                    Ascii("<<\n"); foreach (var (key, item) in dictionary) { Ascii("/" + key + " "); Value(item); Ascii("\n"); } Ascii(">>"); break;
                case string text:
                    Ascii("("); stream.WriteByte(0xFE); stream.WriteByte(0xFF);
                    foreach (var b in Encoding.BigEndianUnicode.GetBytes(text)) { if (b is 40 or 41 or 92) stream.WriteByte(92); stream.WriteByte(b); }
                    Ascii(")"); break;
                case bool b: Ascii(b ? "true" : "false"); break;
                case IEnumerable<object> list: Ascii("[ "); foreach (var item in list) { Value(item); Ascii(" "); } Ascii("]"); break;
                case double d: Ascii(d.ToString("0.0########", CultureInfo.InvariantCulture)); break;
                case int n: Ascii(n.ToString(CultureInfo.InvariantCulture)); break;
                default: throw new InvalidOperationException("Unsupported Photoshop engine value.");
            }
        }
        Value(engine);
        return stream.ToArray();
    }

    private sealed record Knot(SKPoint In, SKPoint Anchor, SKPoint Out);

    private static void WriteShape(Output o, Layer layer, Document document)
    {
        var s = layer.Shape!.Clamped(); var w = layer.Pixels!.Width; var h = layer.Pixels.Height;
        var line = s.Kind == ShapeKind.Line;
        var width = line ? s.LineWidth : s.Stroke != null ? Math.Min(s.StrokeWidth, Math.Min(w, h)) : 0;
        var box = new SKRect((float)(width / 2), (float)(width / 2), w - (float)(width / 2), h - (float)(width / 2));
        var radius = s.Kind == ShapeKind.RoundedRectangle ? Math.Max(0, Math.Min(s.CornerRadius, Math.Min(w, h) / 2.0) - width / 2) : 0;
        var knots = new List<Knot>();
        void Corner(float x, float y) { var p = new SKPoint(x, y); knots.Add(new(p, p, p)); }
        if (line)
        {
            Corner((float)(s.StartX * w ?? Math.Min(s.LineWidth, w) / 2), (float)(s.StartY * h ?? Math.Min(s.LineWidth, h) / 2));
            Corner((float)(s.EndX * w ?? w - Math.Min(s.LineWidth, w) / 2), (float)(s.EndY * h ?? h - Math.Min(s.LineWidth, h) / 2));
        }
        else if (s.Kind == ShapeKind.Ellipse)
        {
            var rx = box.Width / 2; var ry = box.Height / 2; var cx = box.MidX; var cy = box.MidY;
            const float k = .55228475f;
            knots.Add(new(new(cx - k * rx, box.Top), new(cx, box.Top), new(cx + k * rx, box.Top)));
            knots.Add(new(new(box.Right, cy - k * ry), new(box.Right, cy), new(box.Right, cy + k * ry)));
            knots.Add(new(new(cx + k * rx, box.Bottom), new(cx, box.Bottom), new(cx - k * rx, box.Bottom)));
            knots.Add(new(new(box.Left, cy + k * ry), new(box.Left, cy), new(box.Left, cy - k * ry)));
        }
        else if (radius > 0)
        {
            var r = (float)radius; var k = r * .55228475f;
            knots.Add(new(new(box.Left + r - k, box.Top), new(box.Left + r, box.Top), new(box.Left + r, box.Top)));
            knots.Add(new(new(box.Right - r, box.Top), new(box.Right - r, box.Top), new(box.Right - r + k, box.Top)));
            knots.Add(new(new(box.Right, box.Top + r - k), new(box.Right, box.Top + r), new(box.Right, box.Top + r)));
            knots.Add(new(new(box.Right, box.Bottom - r), new(box.Right, box.Bottom - r), new(box.Right, box.Bottom - r + k)));
            knots.Add(new(new(box.Right - r + k, box.Bottom), new(box.Right - r, box.Bottom), new(box.Right - r, box.Bottom)));
            knots.Add(new(new(box.Left + r, box.Bottom), new(box.Left + r, box.Bottom), new(box.Left + r - k, box.Bottom)));
            knots.Add(new(new(box.Left, box.Bottom - r + k), new(box.Left, box.Bottom - r), new(box.Left, box.Bottom - r)));
            knots.Add(new(new(box.Left, box.Top + r), new(box.Left, box.Top + r), new(box.Left, box.Top + r - k)));
        }
        else { Corner(box.Left, box.Top); Corner(box.Right, box.Top); Corner(box.Right, box.Bottom); Corner(box.Left, box.Bottom); }
        var m = layer.Matrix;
        o.Additional("vmsk", () =>
        {
            o.U32(3); o.U32(0); o.U16(6); o.Zeros(24); o.U16(8); o.U16(0); o.Zeros(22);
            o.U16(line ? (ushort)3 : (ushort)0); o.U16((ushort)knots.Count); o.U16(1); o.U16(2); o.Zeros(18);
            foreach (var knot in knots)
            {
                o.U16(line ? (ushort)5 : (ushort)2);
                foreach (var local in new[] { knot.In, knot.Anchor, knot.Out })
                {
                    var p = m.MapPoint(local);
                    o.I32(checked((int)Math.Round(p.Y / document.Height * 0x1000000)));
                    o.I32(checked((int)Math.Round(p.X / document.Width * 0x1000000)));
                }
            }
        });
        // Modern Photoshop uses vscg together with vstk (SoCo is its older fill-only equivalent).
        o.Additional("vscg", () => { o.Id("SoCo"); FillDescriptor(s.Fill).Write(o, true); });
        var scale = Math.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
        var stroke = new D("strokeStyle").Long("strokeStyleVersion", 2).Bool("strokeEnabled", line || s.Stroke != null && width > 0)
            .Bool("fillEnabled", !line && s.FillEnabled).Unit("strokeStyleLineWidth", "#Pxl", width * scale)
            .Unit("strokeStyleLineDashOffset", "#Pxl", 0).Double("strokeStyleMiterLimit", 4)
            .Enum("strokeStyleLineCapType", "strokeStyleLineCapType", line ? "strokeStyleRoundCap" : "strokeStyleButtCap")
            .Enum("strokeStyleLineJoinType", "strokeStyleLineJoinType", "strokeStyleMiterJoin")
            .Enum("strokeStyleLineAlignment", "strokeStyleLineAlignment", "strokeStyleAlignCenter")
            .Bool("strokeStyleScaleLock", false).Bool("strokeStyleStrokeAdjust", false).List("strokeStyleLineDashSet", [])
            .Enum("strokeStyleBlendMode", "BlnM", "Nrml").Unit("strokeStyleOpacity", "#Prc", 100)
            .Obj("strokeStyleContent", FillDescriptor(line ? s.Fill : s.Stroke ?? s.Fill)).Double("strokeStyleResolution", document.Resolution);
        o.Additional("vstk", () => stroke.Write(o, true));
        if (!line)
        {
            var origin = new D("null").Long("keyOriginType", s.Kind == ShapeKind.Ellipse ? 5 : s.Kind == ShapeKind.RoundedRectangle ? 2 : 1)
                .Double("keyOriginResolution", document.Resolution).Obj("keyOriginShapeBBox", RectDescriptor(box, "unitRect", "#Pxl").Long("unitValueQuadVersion", 1))
                .Obj("Trnf", MatrixDescriptor(m)).Bool("keyShapeInvalidated", false).Long("keyOriginIndex", 0);
            if (s.Kind == ShapeKind.RoundedRectangle) origin.Obj("keyOriginRRectRadii", new D("radii").Long("unitValueQuadVersion", 1)
                .Unit("topLeft", "#Pxl", radius).Unit("topRight", "#Pxl", radius).Unit("bottomLeft", "#Pxl", radius).Unit("bottomRight", "#Pxl", radius));
            o.Additional("vogk", () => { o.U32(1); new D("null").List("keyDescriptorList", [origin]).Write(o, true); });
        }
    }

    private static D MatrixDescriptor(SKMatrix m) => new D("Trnf").Double("xx", m.ScaleX).Double("xy", m.SkewY)
        .Double("yx", m.SkewX).Double("yy", m.ScaleY).Double("tx", m.TransX).Double("ty", m.TransY);
    private static D RectDescriptor(SKRect r, string kind, string unit) => new D(kind).Unit("Left", unit, r.Left).Unit("Top ", unit, r.Top).Unit("Rght", unit, r.Right).Unit("Btom", unit, r.Bottom);
    private static D FillDescriptor(uint color) => new D("solidColorLayer").Obj("Clr ", new D("RGBC")
        .Double("Rd  ", color >> 16 & 255).Double("Grn ", color >> 8 & 255).Double("Bl  ", color & 255));

    /// <summary>Adobe's typed descriptor, written directly into its enclosing length-delimited block.</summary>
    private sealed class D(string classId)
    {
        private readonly List<(string Key, string Type, Action<Output> Write)> entries = [];
        private D Add(string key, string type, Action<Output> write) { entries.Add((key, type, write)); return this; }
        private static void Key(Output o, string key) { o.U32(key.Length == 4 ? 0 : (uint)key.Length); o.Id(key); }
        public D Long(string key, int value) => Add(key, "long", o => o.I32(value));
        public D Double(string key, double value) => Add(key, "doub", o => o.F64(value));
        public D Bool(string key, bool value) => Add(key, "bool", o => o.U8(value ? (byte)1 : (byte)0));
        public D Text(string key, string value) => Add(key, "TEXT", o => o.Unicode(value));
        public D Unit(string key, string unit, double value) => Add(key, "UntF", o => { o.Id(unit); o.F64(value); });
        public D Enum(string key, string type, string value) => Add(key, "enum", o => { Key(o, type); Key(o, value); });
        public D Raw(string key, byte[] value) => Add(key, "tdta", o => { o.U32((uint)value.Length); o.Bytes(value); });
        public D Obj(string key, D value) => Add(key, "Objc", o => value.Write(o));
        public D List(string key, D[] values) => Add(key, "VlLs", o => { o.U32((uint)values.Length); foreach (var v in values) { o.Id("Objc"); v.Write(o); } });
        public void Write(Output o, bool versioned = false)
        {
            if (versioned) o.U32(16);
            o.Unicode(""); Key(o, classId); o.U32((uint)entries.Count);
            foreach (var e in entries) { Key(o, e.Key); o.Id(e.Type); e.Write(o); }
        }
    }
}
