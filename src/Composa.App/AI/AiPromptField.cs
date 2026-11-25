using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Utilities;
using Composa.AI;

namespace Composa.App.AI;

internal sealed record AiImageMention(int Number, string FileName)
{
    internal string Label => $"image {Number}";
    internal string Token => "@" + Label;
    internal IBrush Color => Palette.AiImageColors[Number - 1];
    internal static int FirstReference(AiTaskKind task) => task == AiTaskKind.GenerateImage ? 1 : 2;
}

internal sealed class AiPromptField : TextBox
{
    private static readonly Regex Mention = new(@"(?<![\w@])@image ([1-7])(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Query = new(@"(?<![\w@])@([\p{L}\d]*(?: \d*)?)$", RegexOptions.CultureInvariant);
    private static readonly Regex Missing = new(@"(?<![\w@])@missing-image-[1-7](?!\w)", RegexOptions.CultureInvariant);
    private IReadOnlyList<AiImageMention> images = [];
    private readonly StackPanel suggestions = Ui.Column(2);
    private readonly Popup picker;
    private AiImageMention[] choices = [];
    private (string Text, int Caret)? dismissed;
    private int replaceFrom, selected;
    private bool refreshQueued;
    private MentionPresenter? presenter;

    internal AiPromptField()
    {
        picker = new Popup
        {
            PlacementTarget = this, Placement = PlacementMode.BottomEdgeAlignedLeft, VerticalOffset = 4, ShouldUseOverlayLayer = true,
            Child = new Border { Background = Palette.PanelRaised, BorderBrush = Palette.Edge, BorderThickness = new Thickness(1),
                Padding = new Thickness(4), CornerRadius = new CornerRadius(5), Child = suggestions }
        };
        Template = new FuncControlTemplate<AiPromptField>((field, scope) =>
        {
            var text = new MentionPresenter(field) { Name = "PART_TextPresenter" };
            text.Bind(TextPresenter.TextProperty, field.GetObservable(TextProperty));
            text.Bind(TextPresenter.TextWrappingProperty, field.GetObservable(TextWrappingProperty));
            text.Bind(TextPresenter.SelectionStartProperty, field.GetObservable(SelectionStartProperty));
            text.Bind(TextPresenter.SelectionEndProperty, field.GetObservable(SelectionEndProperty));
            text.Bind(TextPresenter.CaretIndexProperty, field.GetObservable(CaretIndexProperty));
            text.Bind(TextPresenter.CaretBrushProperty, field.GetObservable(CaretBrushProperty));
            text.Bind(TextPresenter.SelectionBrushProperty, field.GetObservable(SelectionBrushProperty));
            text.Bind(TextPresenter.SelectionForegroundBrushProperty, field.GetObservable(SelectionForegroundBrushProperty));
            scope.Register(text.Name, text);
            field.presenter = text;

            var hint = new TextBlock { Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false };
            hint.Bind(TextBlock.TextProperty, field.GetObservable(PlaceholderTextProperty));
            void RefreshHint() => hint.IsVisible = string.IsNullOrEmpty(text.Text) && string.IsNullOrEmpty(text.PreeditText);
            text.PropertyChanged += (_, change) =>
            {
                if (change.Property == TextPresenter.TextProperty || change.Property == TextPresenter.PreeditTextProperty) RefreshHint();
            };
            RefreshHint();
            var scroll = new ScrollViewer
            {
                Name = "PART_ScrollViewer", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Panel { Children = { hint, text } },
                Cursor = new Cursor(StandardCursorType.Ibeam)
            };
            scope.Register(scroll.Name, scroll);
            var border = new Border { Name = "PART_BorderElement", Child = scroll };
            border.Bind(Border.BackgroundProperty, field.GetObservable(BackgroundProperty));
            border.Bind(Border.BorderBrushProperty, field.GetObservable(BorderBrushProperty));
            border.Bind(Border.BorderThicknessProperty, field.GetObservable(BorderThicknessProperty));
            border.Bind(Border.CornerRadiusProperty, field.GetObservable(CornerRadiusProperty));
            border.Bind(Border.PaddingProperty, field.GetObservable(PaddingProperty));
            scope.Register(border.Name, border);
            if (field.picker.Parent is Panel previous) previous.Children.Remove(field.picker);
            return new Panel { Children = { border, field.picker } };
        });
        TextChanged += (_, _) => QueueSuggestions();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == CaretIndexProperty || change.Property == SelectionStartProperty || change.Property == SelectionEndProperty)
                QueueSuggestions();
        };
        LostFocus += (_, _) => picker.IsOpen = false;
        DetachedFromVisualTree += (_, _) => picker.IsOpen = false;
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    internal void SetImages(IReadOnlyList<AiImageMention> orderedImages)
    {
        images = orderedImages;
        presenter?.RefreshMentions();
        UpdateSuggestions();
    }

    internal bool HandleSuggestionKey(KeyEventArgs e)
    {
        if (!picker.IsOpen || e.KeyModifiers != KeyModifiers.None) return false;
        switch (e.Key)
        {
            case Key.Escape:
                dismissed = (Text ?? "", CaretIndex);
                picker.IsOpen = false;
                return true;
            case Key.Down:
            case Key.Up:
                selected = (selected + choices.Length + (e.Key == Key.Down ? 1 : -1)) % choices.Length;
                HighlightChoice();
                return true;
            case Key.Tab:
            case Key.Enter:
                Insert(choices[selected]);
                return true;
            default:
                return false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HandleSuggestionKey(e)) e.Handled = true;
        else base.OnKeyDown(e);
    }

    private void QueueSuggestions()
    {
        if (refreshQueued) return;
        refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            refreshQueued = false;
            UpdateSuggestions();
        });
    }

    private void UpdateSuggestions()
    {
        var text = Text ?? "";
        var caret = Math.Clamp(CaretIndex, 0, text.Length);
        if (!IsFocused || SelectionStart != SelectionEnd || dismissed == (text, caret))
        { picker.IsOpen = false; return; }

        var query = Query.Match(text[..caret]);
        if (query.Success)
        {
            var prefix = query.Groups[1].Value;
            var matches = images.Where(image => image.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                image.Number.ToString().StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (matches.Length > 0)
            {
                replaceFrom = query.Index;
                if (!picker.IsOpen || !choices.SequenceEqual(matches))
                {
                    choices = matches;
                    selected = 0;
                    suggestions.Children.Clear();
                    foreach (var option in choices)
                    {
                        var name = Ui.Label(option.FileName, Palette.Secondary, 11);
                        name.MaxWidth = 180;
                        name.TextTrimming = TextTrimming.CharacterEllipsis;
                        var button = new Button { Classes = { "flat" }, Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch,
                            Padding = new Thickness(7, 4), Content = Ui.Row(9, Ui.Label(option.Token, option.Color, 12), name) };
                        button.Click += (_, _) => Insert(option);
                        suggestions.Children.Add(button);
                    }
                    HighlightChoice();
                }
                picker.IsOpen = true;
                return;
            }
        }
        picker.IsOpen = false;
    }

    private void HighlightChoice()
    {
        for (var index = 0; index < suggestions.Children.Count; index++)
            ((Button)suggestions.Children[index]).Background = index == selected ? Palette.Selected : Brushes.Transparent;
    }

    private void Insert(AiImageMention option)
    {
        var caret = CaretIndex;
        var text = Text ?? "";
        var end = caret;
        var existing = Mention.Match(text, replaceFrom);
        if (existing.Success && existing.Index == replaceFrom) end = Math.Max(end, existing.Index + existing.Length);
        SelectionStart = replaceFrom;
        SelectionEnd = end;
        SelectedText = option.Token + (end == text.Length || !char.IsWhiteSpace(text[end]) ? " " : "");
        dismissed = (Text ?? "", CaretIndex);
        picker.IsOpen = false;
    }

    internal static string AfterRemoval(string prompt, int number) => Mention.Replace(prompt, match =>
    {
        var slot = int.Parse(match.Groups[1].Value);
        return slot < number ? match.Value : slot == number ? $"@missing-image-{slot}" : $"@image {slot - 1}";
    });

    internal static string ForModel(string prompt, int first, int count)
    {
        if (Missing.IsMatch(prompt)) throw new InvalidOperationException("Replace the missing image links in the prompt.");
        return Mention.Replace(prompt, match =>
        {
            var slot = int.Parse(match.Groups[1].Value);
            if (slot < first || slot >= first + count)
                throw new InvalidOperationException($"Attach or replace the reference for {match.Value}.");
            return $"image {slot}";
        });
    }

    private sealed class MentionPresenter(AiPromptField field) : TextPresenter
    {
        private double width;
        internal void RefreshMentions() => InvalidateTextLayout();

        protected override Size MeasureOverride(Size availableSize)
        {
            width = availableSize.Width;
            return base.MeasureOverride(availableSize);
        }

        protected override TextLayout CreateTextLayout()
        {
            if (string.IsNullOrEmpty(Text) || !string.IsNullOrEmpty(PreeditText) || SelectionStart != SelectionEnd)
                return base.CreateTextLayout();
            var type = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
            var styles = new List<ValueSpan<TextRunProperties>>();
            foreach (Match mention in Mention.Matches(Text))
            {
                var number = int.Parse(mention.Groups[1].Value);
                if (field.images.FirstOrDefault(image => image.Number == number) is { } image)
                    styles.Add(new ValueSpan<TextRunProperties>(mention.Index, mention.Length,
                        new GenericTextRunProperties(type, FontSize, foregroundBrush: image.Color, fontFeatures: FontFeatures)));
            }
            if (styles.Count == 0) return base.CreateTextLayout();
            return new TextLayout(Text, type, FontSize, Foreground, TextAlignment, TextWrapping,
                flowDirection: FlowDirection, maxWidth: width > 0 ? width : double.PositiveInfinity,
                lineHeight: LineHeight, letterSpacing: LetterSpacing, fontFeatures: FontFeatures, textStyleOverrides: styles);
        }
    }
}
