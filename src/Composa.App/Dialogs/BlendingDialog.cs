using Avalonia;
using Avalonia.Controls;
using Composa.Editing;
using Composa.Model;

namespace Composa.App.Dialogs;

public static class BlendingDialog
{
    public static Task<bool> Edit(Window owner, EditorSession session, Layer layer)
    {
        var ranges = layer.BlendIf;
        var fill = layer.FillOpacity;
        var channel = 0;
        var controls = new StackPanel { Spacing = 10 };
        var rows = new StackPanel { Spacing = 8 };
        void Preview() => session.PreviewBlending(layer, fill, ranges);
        BlendChannelRanges Current() => channel switch { 1 => ranges.Red, 2 => ranges.Green, 3 => ranges.Blue, _ => ranges.Gray };
        void Set(BlendChannelRanges value)
        {
            ranges = channel switch { 1 => ranges with { Red = value }, 2 => ranges with { Green = value }, 3 => ranges with { Blue = value }, _ => ranges with { Gray = value } };
            Preview();
        }
        void Build()
        {
            rows.Children.Clear();
            foreach (var underlying in new[] { false, true })
            {
                rows.Children.Add(Ui.Label(underlying ? "Underlying Layer" : "This Layer"));
                var fields = new Controls.SliderField[4];
                var names = new[] { "Black", "Black fade end", "White fade start", "White" };
                void Refresh()
                {
                    var r = underlying ? Current().Underlying : Current().ThisLayer;
                    var values = new[] { r.Black, r.BlackEnd, r.WhiteStart, r.White };
                    for (var i = 0; i < 4; i++) fields[i].Value = values[i];
                }
                for (var index = 0; index < 4; index++)
                {
                    var at = index;
                    var field = new Controls.SliderField(names[index], 0, 0, 255) { Width = 330, Reset = index < 2 ? 0 : 255 };
                    fields[index] = field;
                    field.Changed += value =>
                    {
                        var pair = Current();
                        var r = underlying ? pair.Underlying : pair.ThisLayer;
                        r = at switch
                        {
                            0 => r with { Black = value, BlackEnd = Math.Max(value, r.BlackEnd), WhiteStart = Math.Max(value, r.WhiteStart), White = Math.Max(value, r.White) },
                            1 => r with { Black = Math.Min(value, r.Black), BlackEnd = value, WhiteStart = Math.Max(value, r.WhiteStart), White = Math.Max(value, r.White) },
                            2 => r with { Black = Math.Min(value, r.Black), BlackEnd = Math.Min(value, r.BlackEnd), WhiteStart = value, White = Math.Max(value, r.White) },
                            _ => r with { Black = Math.Min(value, r.Black), BlackEnd = Math.Min(value, r.BlackEnd), WhiteStart = Math.Min(value, r.WhiteStart), White = value }
                        };
                        Set(underlying ? pair with { Underlying = r } : pair with { ThisLayer = r });
                        Refresh();
                    };
                    rows.Children.Add(field);
                }
                Refresh();
            }
        }
        var fillField = Ui.SliderField("Fill", fill * 100, 0, 100, value => { fill = value / 100; Preview(); }, width: 330, reset: 100);
        fillField.IsEnabled = layer.Pixels != null;
        controls.Children.Add(fillField);
        controls.Children.Add(Ui.Combo(new[] { "Gray", "Red", "Green", "Blue" }, "Gray", v => v, value => { channel = Array.IndexOf(new[] { "Gray", "Red", "Green", "Blue" }, value); Build(); }, 150));
        controls.Children.Add(new TextBlock { Text = "Separate fade handles soften the black and white cutoffs.\nGray and RGB ranges combine; edits remain reversible.", Foreground = Palette.Secondary });
        controls.Children.Add(rows);
        var reset = new Button { Content = "Reset Blend If" };
        reset.Click += (_, _) => { ranges = new(); Build(); Preview(); };
        controls.Children.Add(reset);
        Build();
        return new DialogWindow("Blending Options", controls).Ask(owner);
    }
}
