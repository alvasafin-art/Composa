using Avalonia.Controls;
using Composa.Filters;

namespace Composa.App.Dialogs;

public static partial class AdjustmentDialogs
{
    private static Control ChannelMixerEditor(ChannelMixerAdjustment mixer, Action<Adjustment> update)
    {
        var outputHost = new Border(); var rows = new StackPanel { Spacing = 8 }; var outputIndex = 0; var refreshing = false; ComboBox? presets = null;
        void SyncPreset()
        { if (presets == null) return; refreshing = true; presets.SelectedItem = ChannelMixerAdjustment.Presets.FirstOrDefault(p => p.Value.ContentEquals(mixer)).Key ?? "Custom"; refreshing = false; }
        var total = Ui.Label("", Palette.Secondary); total.Name = "MixerTotal";
        void BuildRows()
        {
            rows.Children.Clear(); var index = mixer.Monochrome ? 3 : outputIndex; var row = mixer.Row(index).Normalized(); var defaults = new ChannelMixerAdjustment().Row(index);
            void Total() => total.Text = $"Total: {row.Total:0.0}%";
            void Set(ChannelMix next) { row = next.Normalized(); update(mixer = mixer.WithRow(index, row)); Total(); SyncPreset(); }
            rows.Children.Add(Ui.SliderField("Red", row.Red, ChannelMix.Min, ChannelMix.Max, v => Set(row with { Red = v }), .1, "0.0", FieldWidth, reset: defaults.Red));
            rows.Children.Add(Ui.SliderField("Green", row.Green, ChannelMix.Min, ChannelMix.Max, v => Set(row with { Green = v }), .1, "0.0", FieldWidth, reset: defaults.Green));
            rows.Children.Add(Ui.SliderField("Blue", row.Blue, ChannelMix.Min, ChannelMix.Max, v => Set(row with { Blue = v }), .1, "0.0", FieldWidth, reset: defaults.Blue));
            rows.Children.Add(Ui.SliderField("Constant", row.Constant, ChannelMix.Min, ChannelMix.Max, v => Set(row with { Constant = v }), .1, "0.0", FieldWidth, reset: defaults.Constant));
            rows.Children.Add(Ui.TextButton("Reset output", () => { update(mixer = mixer.WithRow(index, defaults)); BuildRows(); SyncPreset(); })); Total();
        }
        void BuildOutput()
        {
            var names = mixer.Monochrome ? new[] { "Gray" } : new[] { "Red", "Green", "Blue" };
            var output = Ui.Combo(names, mixer.Monochrome ? "Gray" : names[outputIndex], v => v, v => { if (refreshing) return; if (!mixer.Monochrome) outputIndex = Array.IndexOf(names, v); BuildRows(); }, FieldWidth);
            output.Name = "MixerOutput"; outputHost.Child = output; BuildRows();
        }
        var mono = Ui.Check("Monochrome", mixer.Monochrome, v => { if (refreshing) return; update(mixer = mixer with { Monochrome = v }); BuildOutput(); SyncPreset(); }); mono.Name = "MixerMonochrome";
        void ApplyPreset(ChannelMixerAdjustment next)
        { mixer = next; update(mixer); refreshing = true; mono.IsChecked = mixer.Monochrome; refreshing = false; BuildOutput(); }
        var selected = ChannelMixerAdjustment.Presets.FirstOrDefault(p => p.Value.ContentEquals(mixer)).Key ?? "Custom";
        presets = Ui.Combo(new[] { "Custom" }.Concat(ChannelMixerAdjustment.Presets.Keys), selected, v => v, v => { if (!refreshing && v != "Custom") ApplyPreset(ChannelMixerAdjustment.Presets[v]); }, FieldWidth); presets.Name = "MixerPreset";
        ToolTip.SetTip(total, "Sum of source contributions, excluding Constant. Totals above 100% can clip highlights; negative coefficients subtract source color.");
        BuildOutput(); return Ui.Column(10, presets, mono, Ui.Label("Output channel", Palette.Secondary), outputHost, Ui.Label("Source contributions (%)", Palette.Secondary), rows, total);
    }
}
