using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Composa.Model;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Text;

namespace Composa.App;

public sealed partial class MainWindow
{
    private ComboBox? openFontPicker;

    private Action<string?> EnableFontSamples(ComboBox combo, EditorSession owner, IReadOnlyList<string> families, Action<string> apply)
    {
        combo.IsEditable = true;
        combo.IsTextSearchEnabled = false;
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
            // The editable template also asks for a view when its selection is empty.
            if (string.IsNullOrWhiteSpace(family)) return null;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(Ui.Label(family));
            if (Missing(family))
            {
                var warning = Ui.Label("⚠", Palette.Secondary);
                warning.Margin = new Thickness(8, 0, 0, 0);
                const string message = "Some characters in this text are missing from the font";
                Locale.Tip(warning, message);
                Avalonia.Automation.AutomationProperties.SetName(warning, message);
                AddAt(row, warning, 1);
            }
            return row;
        });
        string? request = null, selectedFamily = null, pendingQuery = null;
        TextBox? editor = null;
        int generation = 0, keyboardIndex = -1;
        bool busy = false, navigating = false, synchronizing = false, filtering = false;
        ComboBoxItem? keyboardHighlight = null;
        void Synchronize(Action action)
        {
            var previous = synchronizing;
            synchronizing = true;
            try { action(); }
            finally { synchronizing = previous; }
        }
        void Highlight(ComboBoxItem? item)
        {
            keyboardHighlight?.Classes.Remove("font-preview");
            keyboardHighlight = item;
            keyboardHighlight?.Classes.Add("font-preview");
        }
        void ShowCurrentFamily()
        {
            Synchronize(() =>
            {
                combo.ItemsSource = families;
                combo.SelectedItem = families.FirstOrDefault(f => string.Equals(f, selectedFamily, StringComparison.OrdinalIgnoreCase));
                combo.Text = selectedFamily ?? "";
            });
        }
        void Close()
        {
            generation++;
            request = pendingQuery = null;
            filtering = false;
            Highlight(null);
            if (openFontPicker == combo) { openFontPicker = null; owner.ClearFontSample(); }
        }
        void Commit(string family)
        {
            if (session != owner || !TextStyle.IsFamilyName(family)) return;
            Synchronize(() =>
            {
                combo.IsDropDownOpen = false;
                apply(family);
                ShowCurrentFamily();
            });
        }
        void Search(string text)
        {
            pendingQuery = null;
            filtering = true;
            navigating = false;
            keyboardIndex = -1;
            Highlight(null);
            generation++;
            request = null;
            owner.ClearFontSample();
            var query = text.Trim();
            var matches = families.Where(f => f.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
                    : f.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2).ToArray();
            var caret = editor?.CaretIndex ?? text.Length;
            Synchronize(() =>
            {
                combo.ItemsSource = matches.Length == 0
                    ? new object[] { new ComboBoxItem { Content = Locale.T("No matching fonts."), IsEnabled = false } } : matches;
                combo.SelectedIndex = -1;
                combo.Text = text;
                combo.IsDropDownOpen = true;
                if (editor != null) editor.CaretIndex = Math.Min(caret, text.Length);
            });
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
            {
                keyboardIndex = combo.IndexFromContainer(item);
                Highlight(null);
                Sample(family);
            }
        }
        combo.ContainerPrepared += (_, e) =>
        {
            e.Container.PointerEntered -= PointAt; e.Container.PointerEntered += PointAt;
            e.Container.PointerMoved -= PointAt; e.Container.PointerMoved += PointAt;
        };
        void StopWheel(object? sender, PointerWheelEventArgs e)
        {
            if (combo.IsDropDownOpen) e.Handled = true;
        }
        combo.AddHandler(PointerWheelChangedEvent, StopWheel, RoutingStrategies.Tunnel, true);
        void EditorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != TextBox.TextProperty || synchronizing || editor is not { IsFocused: true }) return;
            var text = editor.Text ?? "";
            if (combo.SelectedItem is string selection && text == selection) return;
            pendingQuery = text;
            Dispatcher.UIThread.Post(() =>
            {
                if (pendingQuery == text && combo.IsAttachedToVisualTree()) Search(text);
            }, DispatcherPriority.Input);
        }
        combo.TemplateApplied += (_, e) =>
        {
            if (editor != null) editor.PropertyChanged -= EditorChanged;
            editor = e.NameScope.Find<TextBox>("PART_EditableTextBox");
            if (editor != null) editor.PropertyChanged += EditorChanged;
            if (e.NameScope.Find<Popup>("PART_Popup")?.Child is Control popup)
                popup.AddHandler(PointerWheelChangedEvent, StopWheel, RoutingStrategies.Tunnel, true);
        };
        combo.SelectionChanged += (_, e) =>
        {
            if (!synchronizing && ReferenceEquals(e.Source, combo) && e.AddedItems.OfType<string>().FirstOrDefault() is { } family)
                Commit(family);
        };
        void HandleFontKey(object? sender, KeyEventArgs e)
        {
            if (pendingQuery is { } query && e.Key is Key.Enter or Key.Up or Key.Down) Search(query);
            if (!combo.IsDropDownOpen) return;
            if (e.Key is Key.Up or Key.Down)
            {
                e.Handled = true;
                if (combo.Items.Count == 0 || combo.Items[0] is not string) return;
                navigating = true;
                keyboardIndex = Math.Clamp(keyboardIndex + (e.Key == Key.Down ? 1 : -1), 0, combo.ItemCount - 1);
                combo.ScrollIntoView(keyboardIndex);
                Highlight(combo.ContainerFromIndex(keyboardIndex) as ComboBoxItem);
                Sample(combo.Items[keyboardIndex] as string);
            }
            else if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                if (e.Key == Key.Enter)
                {
                    int index = navigating ? keyboardIndex : filtering ? 0 : combo.SelectedIndex;
                    if (index >= 0 && index < combo.ItemCount && combo.Items[index] is string family) { Commit(family); return; }
                }
                combo.IsDropDownOpen = false;
            }
        }
        combo.AddHandler(KeyDownEvent, HandleFontKey, RoutingStrategies.Tunnel);
        combo.DropDownOpened += (_, _) =>
        {
            openFontPicker = combo;
            navigating = false;
            keyboardIndex = filtering ? -1 : combo.SelectedIndex;
        };
        combo.DropDownClosed += (_, _) =>
        {
            Close();
            Dispatcher.UIThread.Post(() =>
            {
                if (!combo.IsDropDownOpen && pendingQuery == null && combo.IsAttachedToVisualTree()) ShowCurrentFamily();
            }, DispatcherPriority.Input);
        };
        combo.DetachedFromVisualTree += (_, _) => Close();
        return family =>
        {
            selectedFamily = family;
            combo.PlaceholderText = family ?? Locale.T("(Multiple)");
            if (!combo.IsDropDownOpen && pendingQuery == null) ShowCurrentFamily();
        };
    }
}
