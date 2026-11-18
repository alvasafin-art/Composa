using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Composa.AI;

namespace Composa.App.AI;

internal sealed class AiVariantButtons : StackPanel
{
    private readonly ToggleButton two;
    private readonly ToggleButton three;
    private bool updating;

    public int Count
    {
        get => two.IsChecked == true ? 2 : three.IsChecked == true ? 3 : 1;
        set
        {
            updating = true;
            two.IsChecked = value == 2;
            three.IsChecked = value == 3;
            updating = false;
        }
    }

    public AiVariantButtons(int initial, Action<int> changed)
    {
        Orientation = Orientation.Horizontal;
        Spacing = 2;
        two = AddChoice(2); three = AddChoice(3);
        Count = initial;

        ToggleButton AddChoice(int number)
        {
            var button = new ToggleButton { Name = "AiVariants" + number, Content = number.ToString(),
                Width = 30, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            button.Classes.Add("tool");
            AutomationProperties.SetName(button, $"{number} variants");
            ToolTip.SetTip(button, $"Select {number} variants · click again for one");
            button.IsCheckedChanged += (_, _) =>
            {
                if (updating) return;
                Count = button.IsChecked == true ? number : 1;
                changed(Count);
            };
            Children.Add(button);
            return button;
        }
    }
}

internal static class AiGenerationControls
{
    internal static TextBox PromptField(string placeholder)
    {
        var field = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 2, MinHeight = 50, MaxHeight = 104,
            Padding = new Thickness(9, 5), CornerRadius = new CornerRadius(4), PlaceholderText = placeholder
        };
        field.Resources["TextControlBackgroundFocused"] = Palette.Window;
        field.Resources["TextControlBorderBrushFocused"] = Palette.Secondary;
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
        ToolTip.SetTip(grip, "Drag to move");
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

    internal static StackPanel RunGroup(Button run, AiVariantButtons variants)
    {
        run.Name = "AiRun";
        run.Classes.Remove("accent");
        run.Background = Palette.PanelRaised;
        run.BorderBrush = Palette.Secondary;
        run.BorderThickness = new Thickness(1);
        run.CornerRadius = new CornerRadius(4);
        run.Content = Ui.Row(5, Icons.Create(Icons.Run, 12, Brushes.White), Ui.Label("Run", Brushes.White));
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
            var name = Ui.Label(model.Name);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = details ? 310 : 155;
            ToolTip.SetTip(name, model.Name == model.Identifier ? model.Name : $"{model.Name}\n{model.Identifier}");
            if (!details) return name;
            return Ui.Column(3, name, Ui.Label(model.Engine.PaidApi ? "Comfy credits" : "ComfyUI server", Palette.Secondary, 11));
        }
        picker.ItemsSource = models;
        picker.ItemTemplate = new FuncDataTemplate<ImageModelChoice>((item, _) => Row(item, true));
        picker.SelectionBoxItemTemplate = new FuncDataTemplate<ImageModelChoice>((item, _) => Row(item, false));
        StyleModelField(picker, "Image model");
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
        picker.Height = 30; picker.MinHeight = 0; picker.CornerRadius = new CornerRadius(6);
        picker.Padding = new Thickness(9, 0, 5, 0);
        picker.VerticalContentAlignment = VerticalAlignment.Center;
        picker.Background = Palette.Panel;
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
