using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Composa.AI;

namespace Composa.App.AI;

internal static class AiGenerationControls
{
    internal static IEnumerable<AiTaskKind> VisibleOperations =>
        DirectImageGenerator.Operations.Where(task => task != AiTaskKind.MatchToScene);

    internal static bool IsOperationEnabled(AiTaskKind task) =>
        task is not (AiTaskKind.MatchToScene or AiTaskKind.Relight);

    internal static void ShowBusy(Button button, bool busy) => button.Classes.Set("ai-busy", busy);

    internal const int EditorSpacing = 8;
    internal const int ModelPickerWidth = 166;

    internal static AiPromptField PromptField(string placeholder)
    {
        var field = new AiPromptField
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 2, MinHeight = 50, MaxHeight = 104,
            Padding = new Thickness(9, 5), CornerRadius = new CornerRadius(4), PlaceholderText = placeholder
        };
        field.Resources["TextControlBackgroundFocused"] = Palette.Window;
        field.Resources["TextControlBorderBrushFocused"] = Palette.Accent;
        field.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
        return field;
    }

    internal static Border DragHandle()
    {
        var marks = Ui.Column(2);
        for (var i = 0; i < 5; i++)
            marks.Children.Add(new Border { Width = 5, Height = 2, Background = Palette.Secondary, CornerRadius = new CornerRadius(1) });
        marks.VerticalAlignment = VerticalAlignment.Center;
        var grip = new Border { Name = "AiDragHandle", Width = 13, Height = 28, Background = Brushes.Transparent,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeAll), Child = marks };
        Locale.Tip(grip, "Drag to move");
        return grip;
    }

    internal static Button IconAction(Icons.Icon icon, string name, string label, Action click)
    {
        var button = Ui.IconButton(icon, label, click, 18);
        button.Name = name; button.Width = 30; button.Height = 28; button.Padding = new Thickness(4);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(button, label);
        return button;
    }

    internal static bool SupportsAlignment(AiTaskKind task) =>
        task is AiTaskKind.ImageEdit or AiTaskKind.GenerativeFill;

    internal static ToggleButton AlignmentToggle(bool enabled, Action<bool> changed)
    {
        var toggle = new ToggleButton { Name = "AiPixelAlignment", Classes = { "tool" },
            Width = 30, Height = 28, Padding = new Thickness(4), IsChecked = enabled,
            Content = Icons.Create(Icons.PixelAlignment, 18) };
        AutomationProperties.SetName(toggle, Locale.T("Align pixels"));
        Locale.Tip(toggle, "Align pixels · use unchanged image details to correct small shifts before inserting the result.");
        toggle.Click += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    internal static bool SupportsVariants(AiTaskKind task) =>
        task is not (AiTaskKind.Upscale or AiTaskKind.SelectSubject or AiTaskKind.ObjectSelection);

    internal static StackPanel QuickRuns(Action<int> launch)
    {
        var group = Ui.Row(2);
        foreach (var count in new[] { 2, 3 })
        {
            var button = Ui.TextButton(count.ToString(), () => launch(count));
            button.Name = $"AiVariants{count}";
            button.Classes.Add("flat");
            button.MinWidth = 0; button.Width = 24; button.Height = 28; button.Padding = new Thickness(0);
            button.VerticalContentAlignment = VerticalAlignment.Center;
            var label = $"Run {count} variants";
            AutomationProperties.SetName(button, label);
            Locale.Tip(button, label);
            group.Children.Add(button);
        }
        return group;
    }

    internal static StackPanel RunGroup(Button run, StackPanel variants)
    {
        run.Name = "AiRun";
        run.Resources["AccentButtonBackground"] = Palette.Active;
        run.Resources["AccentButtonBackgroundPointerOver"] = Palette.ActiveHover;
        run.Resources["AccentButtonBackgroundPressed"] = Palette.ActivePressed;
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
            run.Resources["AccentButtonForeground" + state] = Palette.ActionText;
        run.BorderThickness = new Thickness(0);
        run.CornerRadius = new CornerRadius(4);
        run.Content = Ui.Row(3, Icons.Create(Icons.Run, 14, Palette.ActionText),
            new TextBlock { Text = Locale.T("Run"), FontWeight = FontWeight.SemiBold });
        run.MinHeight = run.Height = 28; run.MinWidth = 64; run.Padding = new Thickness(9, 0);
        run.HorizontalContentAlignment = HorizontalAlignment.Center; run.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(run, "Run");
        return Ui.Row(4, run, variants);
    }

    internal static void PresentImageModels(ComboBox picker, ImageModelChoice[] models)
    {
        // Items carry the route and server identifier even when two files share a display name.
        Control Row(ImageModelChoice? model, bool details)
        {
            if (model == null) return new TextBlock();
            var description = details ? Locale.T(model.Name) : $"{Locale.T(model.Name)} · {Locale.T(model.ConnectionLabel)}";
            if (!model.Available) description += " · " + Locale.T("Unavailable");
            var name = Ui.Label(description, size: details ? null : 12);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            Locale.Tip(name, $"{Locale.T(model.ConnectionLabel)}\n{model.Identifier}");
            return name;
        }
        var items = new List<object>();
        foreach (var group in models.GroupBy(model => model.ConnectionLabel))
        {
            items.Add(new ComboBoxItem
            {
                IsEnabled = false, IsHitTestVisible = false, Focusable = false,
                Content = new TextBlock
                {
                    Text = Locale.T(group.Key), FontWeight = FontWeight.SemiBold,
                    Foreground = Palette.Secondary, FontSize = 10
                }
            });
            items.AddRange(group);
        }
        picker.ItemsSource = items;
        picker.ItemTemplate = new FuncDataTemplate<ImageModelChoice>((item, _) => Row(item, true));
        picker.SelectionBoxItemTemplate = new FuncDataTemplate<ImageModelChoice>((item, _) => Row(item, false));
        StyleModelField(picker, "Image model");
        picker.Padding = new Thickness(5, 0, 1, 0);
    }

    internal static void StyleModelPicker(ComboBox picker, IReadOnlyList<EngineProfile> packs, Func<EngineProfile, string> modelName)
    {
        Control Row(string? name, bool detail)
        {
            var pack = packs.FirstOrDefault(p => p.DisplayName == name);
            var title = Ui.Label(pack == null ? name ?? "" : modelName(pack));
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            if (pack == null) return title;
            Control text = detail ? Ui.Column(2, title, Ui.Label(pack.PaidApi ? "Comfy credits" : "ComfyUI server", Palette.Secondary)) : title;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,8,*"), Margin = new Thickness(0, detail ? 3 : 0) };
            row.Children.Add(Icons.Create(Icons.Layers, 16, pack.PaidApi ? Palette.Accent : Palette.Secondary));
            Grid.SetColumn(text, 2); row.Children.Add(text);
            return row;
        }
        picker.ItemTemplate = new FuncDataTemplate<string>((name, _) => Row(name, true));
        picker.SelectionBoxItemTemplate = new FuncDataTemplate<string>((name, _) => Row(name, false));
        StyleModelField(picker, "Image model");
    }

    internal static void StyleModelField(ComboBox picker, string label)
    {
        picker.Classes.Add("model-field");
        picker.Height = 30; picker.MinHeight = 0; picker.CornerRadius = new CornerRadius(6);
        picker.Padding = new Thickness(9, 0, 5, 0);
        picker.VerticalContentAlignment = VerticalAlignment.Center;
        picker.Background = Palette.Field;
        AutomationProperties.SetName(picker, label);
    }
}

/// <summary>Align the action group to the right, moving it below the tools when the canvas is narrow.</summary>
internal sealed class AiPromptToolbar : Panel
{
    private readonly Control tools;
    private readonly Control actions;
    private const double Gap = 5;

    public AiPromptToolbar(Control tools, Control actions)
    {
        this.tools = tools;
        this.actions = actions;
        Children.Add(tools);
        Children.Add(actions);
    }

    protected override Size MeasureOverride(Size available)
    {
        tools.Measure(available);
        actions.Measure(available);
        var width = tools.DesiredSize.Width + Gap + actions.DesiredSize.Width;
        var height = width > available.Width ? tools.DesiredSize.Height + Gap + actions.DesiredSize.Height
            : Math.Max(tools.DesiredSize.Height, actions.DesiredSize.Height);
        return new Size(Math.Min(width, available.Width), height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        var left = tools.DesiredSize;
        var right = actions.DesiredSize;
        var wrap = left.Width + Gap + right.Width > size.Width;
        tools.Arrange(new Rect(0, 0, Math.Min(left.Width, size.Width), left.Height));
        actions.Arrange(new Rect(Math.Max(0, size.Width - right.Width), wrap ? left.Height + Gap : 0,
            Math.Min(right.Width, size.Width), right.Height));
        return size;
    }
}
