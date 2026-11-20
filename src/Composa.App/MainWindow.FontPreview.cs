using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Composa.Model;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Text;

namespace Composa.App;

public sealed partial class MainWindow
{
    private ComboBox? openFontPicker;

    private void EnableFontSamples(ComboBox combo, EditorSession owner)
    {
        combo.IsEditable = true;
        string? sourceText = null;
        int[] characters = [];
        var coverage = new Dictionary<TextFace, bool>();
        bool Missing(string family)
        {
            var style = owner.CurrentTextStyle;
            if (style.Text != sourceText)
            {
                sourceText = style.Text;
                characters = sourceText.EnumerateRunes().Where(r => !Rune.IsWhiteSpace(r) &&
                    Rune.GetUnicodeCategory(r) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format)
                    && r.Value is not (>= 0xFE00 and <= 0xFE0F) and not (>= 0xE0100 and <= 0xE01EF)).Select(r => r.Value).Distinct().ToArray();
                coverage.Clear();
            }
            var face = FontCatalog.Closest(family, owner.CurrentTextFace).Face;
            if (coverage.TryGetValue(face, out var missing)) return missing;
            var typeface = TextLayout.TypefaceFor(face);
            return coverage[face] = characters.Any(code => !typeface.ContainsGlyph(code));
        }
        combo.ItemTemplate = new FuncDataTemplate<string>((family, _) =>
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(Ui.Label(family));
            if (Missing(family))
            {
                var warning = Ui.Label("⚠", Palette.Secondary);
                warning.Margin = new Thickness(8, 0, 0, 0);
                const string message = "Some characters in this text are missing from the font";
                ToolTip.SetTip(warning, message);
                Avalonia.Automation.AutomationProperties.SetName(warning, message);
                AddAt(row, warning, 1);
            }
            return row;
        });
        string? request = null;
        int generation = 0, keyboardIndex = -1;
        bool busy = false, navigating = false;
        void Close()
        {
            generation++;
            request = null;
            if (openFontPicker == combo) { openFontPicker = null; owner.ClearFontSample(); }
        }
        async void Sample(string? family)
        {
            if (family == null || !combo.IsDropDownOpen || session != owner || family == request) return;
            request = family; generation++;
            if (busy) return;
            busy = true;
            try
            {
                while (request is { } next && owner.ActiveLayer is { Text: not null } layer)
                {
                    int version = generation;
                    var original = owner.CurrentTextStyle;
                    var start = owner.TextEdit?.SelectionStart ?? 0;
                    var end = owner.TextEdit?.SelectionEnd ?? 0;
                    var depth = owner.Document.WorkingDepth; var profile = owner.Document.Profile;
                    var sample = await Task.Run(() =>
                    {
                        var style = original.WithFace(f => FontCatalog.Closest(next, f).Face, start, end);
                        return (style, pixels: EditorSession.RenderText(style, depth, profile));
                    });
                    if (version == generation && session == owner && owner.ActiveLayer == layer &&
                        ReferenceEquals(owner.CurrentTextStyle, original) && combo.IsDropDownOpen)
                        owner.ShowFontSample(layer, sample.style, sample.pixels);
                    else sample.pixels.Dispose();
                    if (version == generation) break;
                }
            }
            catch (Exception ex) { if (combo.IsDropDownOpen) ShowProblem(ex.Message); }
            finally { busy = false; }
        }
        void PointAt(object? sender, PointerEventArgs e)
        {
            if (sender is ComboBoxItem { Content: string family } item)
            { navigating = true; keyboardIndex = combo.IndexFromContainer(item); Sample(family); }
        }
        combo.ContainerPrepared += (_, e) =>
        {
            e.Container.PointerEntered -= PointAt; e.Container.PointerEntered += PointAt;
            e.Container.PointerMoved -= PointAt; e.Container.PointerMoved += PointAt;
        };
        combo.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!combo.IsDropDownOpen) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!combo.IsDropDownOpen) return;
                foreach (var container in combo.GetRealizedContainers().OfType<ComboBoxItem>())
                {
                    if (!new Avalonia.Rect(container.Bounds.Size).Contains(e.GetPosition(container))) continue;
                    PointAt(container, e);
                    break;
                }
            }, DispatcherPriority.Loaded);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        combo.AddHandler(TextInputEvent, (_, _) => navigating = false, RoutingStrategies.Tunnel, true);
        void HandleFontKey(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !navigating && !string.IsNullOrWhiteSpace(combo.Text))
            {
                string query = combo.Text.Trim();
                var match = combo.Items.OfType<string>().FirstOrDefault(f => f.Equals(query, StringComparison.OrdinalIgnoreCase))
                    ?? combo.Items.OfType<string>().FirstOrDefault(f => f.StartsWith(query, StringComparison.OrdinalIgnoreCase));
                if (match != null) { combo.SelectedItem = match; combo.Text = match; }
                else combo.Text = combo.SelectedItem as string ?? owner.CurrentTextFace.FontFamily;
                combo.IsDropDownOpen = false;
                e.Handled = true;
                return;
            }
            if (!combo.IsDropDownOpen) return;
            if (e.Key is Key.Up or Key.Down)
            {
                navigating = true;
                keyboardIndex = Math.Clamp(keyboardIndex + (e.Key == Avalonia.Input.Key.Down ? 1 : -1), 0, combo.ItemCount - 1);
                combo.ScrollIntoView(keyboardIndex);
                combo.ContainerFromIndex(keyboardIndex)?.Focus();
                Sample(combo.Items[keyboardIndex] as string); e.Handled = true;
            }
            else if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                if (e.Key == Avalonia.Input.Key.Enter && keyboardIndex >= 0) combo.SelectedIndex = keyboardIndex;
                combo.IsDropDownOpen = false;
            }
        }
        combo.AddHandler(KeyDownEvent, HandleFontKey, RoutingStrategies.Tunnel);
        combo.DropDownOpened += (_, _) => { openFontPicker = combo; navigating = false; keyboardIndex = combo.SelectedIndex; };
        combo.DropDownClosed += (_, _) => Close();
        combo.DetachedFromVisualTree += (_, _) => Close();
    }
}
