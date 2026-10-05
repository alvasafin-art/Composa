using Composa.Model;

namespace Composa.IO.Psd;

/// <summary>Original reader of Adobe's lrFX/lfx2/lmfx records. Unsupported parameters are reported.</summary>
internal static class PsdEffects
{
    public static LayerEffects? Read(PsdLayer layer, double globalAngle, List<PsdConversion> conversions)
    {
        void Note(string text) => conversions.Add(new(layer.Name, text));
        foreach (var key in new[] { "lmfx", "lfx2" })
        {
            if (!layer.Extra.TryGetValue(key, out var bytes)) continue;
            try
            {
                var c = new PsdCursor(bytes); _ = c.U32();
                if (PsdDescriptor.ReadVersioned(ref c) is { } d)
                {
                    var scale = (PsdDescriptor.Number(d, "Scl ") ?? 100) / 100;
                    scale = Math.Clamp(scale, .01, 10);
                    var master = PsdDescriptor.Flag(d, "masterFXSwitch") ?? true;
                    var effects = LayerEffects.Empty;
                    foreach (var (effectKey, values) in d)
                    {
                        var list = values is Dictionary<string, object?> one ? new List<object?> { one } : values as List<object?>;
                        if (list == null) continue;
                        var recognized = effectKey is "DrSh" or "dropShadowMulti" or "IrSh" or "innerShadowMulti" or "SoFi" or "solidFillMulti" or "OrGl" or "IrGl" or "FrFX" or "frameFXMulti";
                        if (!recognized) { if (effectKey is "ebbl" or "ChFX" or "GrFl" or "PtFl") Note($"Photoshop effect {effectKey} is not supported and was skipped."); continue; }
                        var count = 0;
                        foreach (var value in list.OfType<Dictionary<string, object?>>())
                        {
                            if (++count > 1) { Note($"Multiple {effectKey} effects are not supported; the first was kept."); break; }
                            var enabled = master && (PsdDescriptor.Flag(value, "enab") ?? true);
                            var opacity = (PsdDescriptor.Number(value, "Opct") ?? 100) / 100;
                            var color = PsdDescriptor.Color(PsdDescriptor.Child(value, "Clr ")) ?? 0xFF000000;
                            var blend = PsdDescriptor.Enumeration(value, "Md  ");
                            if (enabled && blend is not (null or "Nrml") && !(blend == "Mltp" && color == 0xFF000000)) Note($"The {effectKey} blend mode is approximated using Normal.");
                            if ((PsdDescriptor.Number(value, "Ckmt") ?? 0) != 0 || (PsdDescriptor.Number(value, "Nose") ?? 0) != 0 || PsdDescriptor.Child(value, "TrnS") != null)
                                Note($"The {effectKey} spread, noise or custom contour is approximated.");
                            double N(string name, double fallback) => (PsdDescriptor.Number(value, name) ?? fallback) * scale;
                            if (effectKey is "DrSh" or "dropShadowMulti" or "IrSh" or "innerShadowMulti")
                            {
                                var shadow = new ShadowEffect { Enabled = enabled, Color = color, Opacity = opacity,
                                    Angle = PsdDescriptor.Flag(value, "uglg") == true ? globalAngle : PsdDescriptor.Number(value, "lagl") ?? 120,
                                    Distance = N("Dstn", 5), Blur = N("blur", 5) }.Clamped();
                                effects = effectKey is "DrSh" or "dropShadowMulti" ? effects with { Shadow = shadow } : effects with { InnerShadow = shadow };
                            }
                            else if (effectKey is "SoFi" or "solidFillMulti") effects = effects with { ColorOverlay = new ColorOverlayEffect { Enabled = enabled, Color = color, Opacity = opacity }.Clamped() };
                            else if (effectKey == "OrGl") effects = effects with { OuterGlow = new OuterGlowEffect { Enabled = enabled, Color = color, Opacity = opacity, Size = N("blur", 5) }.Clamped() };
                            else if (effectKey == "IrGl") effects = effects with { InnerGlow = new InnerGlowEffect { Enabled = enabled, Color = color, Opacity = opacity, Size = N("blur", 5) }.Clamped() };
                            else
                            {
                                var position = PsdDescriptor.Enumeration(value, "Styl");
                                if (position == "CtrF") Note("A centered Photoshop stroke is approximated as an outside stroke.");
                                if (PsdDescriptor.Enumeration(value, "PntT") is not (null or "SClr")) Note("The Photoshop stroke's gradient/pattern fill is approximated with a solid color.");
                                effects = effects with { Stroke = new StrokeEffect { Enabled = enabled, Color = color, Opacity = opacity, Size = N("Sz  ", 3), Inside = position == "InsF" }.Clamped() };
                            }
                        }
                    }
                    if (!effects.IsEmpty) return effects;
                    Note("The Photoshop layer effects contain no supported effects.");
                    return null;
                }
            }
            catch (PsdException) { }
            Note("The Photoshop layer effects could not be read.");
        }
        if (!layer.Extra.TryGetValue("lrFX", out var legacy)) return null;
        try
        {
            var c = new PsdCursor(legacy); _ = c.U16(); var count = c.U16();
            var effects = LayerEffects.Empty;
            for (var i = 0; i < count; i++)
            {
                if (c.Ascii(4) != "8BIM") throw PsdException.Truncated();
                var key = c.Ascii(4); var size = c.U32(); var e = new PsdCursor(c.Bytes(size));
                if (key is "dsdw" or "isdw")
                {
                    _ = e.U32(); var blur = e.U32(); var intensity = e.U32(); var angle = e.I32(); var distance = e.U32();
                    var space = e.U16(); var r = e.U16(); var g = e.U16(); var b = e.U16(); e.Skip(2);
                    if (space != 0) { Note("A legacy Photoshop shadow uses an unsupported color space."); continue; }
                    e.Skip(4); var blend = e.Ascii(4); var enabled = e.U8() != 0; var global = e.U8() != 0; var opacity = e.U8();
                    var shadow = new ShadowEffect { Enabled = enabled, Blur = blur, Distance = distance, Angle = global ? globalAngle : angle,
                        Opacity = opacity / 255.0, Color = 0xFF000000u | (uint)(r >> 8) << 16 | (uint)(g >> 8) << 8 | (uint)(b >> 8) }.Clamped();
                    if (intensity != 0 || blend is not ("norm" or "mul ")) Note("The legacy shadow's spread or blending is approximated.");
                    effects = key == "dsdw" ? effects with { Shadow = shadow } : effects with { InnerShadow = shadow };
                }
                else if (key != "cmnS") Note($"Legacy Photoshop effect {key} is not supported and was skipped.");
            }
            return effects.IsEmpty ? null : effects;
        }
        catch (PsdException) { Note("The legacy Photoshop effects could not be read."); return null; }
    }
}
