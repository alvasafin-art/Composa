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
        run.Content = Ui.Row(5, Icons.Create(Icons.Run, 12, Brushes.White), Ui.Label("Run", Brushes.White));
        run.MinHeight = run.Height = 28; run.MinWidth = 64; run.Padding = new Thickness(9, 0);
        run.HorizontalContentAlignment = HorizontalAlignment.Center; run.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(run, "Run");
        return Ui.Row(4, run, variants);
    }

    internal static void StyleModelPicker(ComboBox picker, IReadOnlyList<EngineProfile> packs)
    {
        Control Row(string? name, bool detail)
        {
            var pack = packs.FirstOrDefault(p => p.DisplayName == name);
            var title = Ui.Label(pack?.Id == "chatgpt-image-2.5" ? "GPT Image 2.5" : name ?? "");
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            if (pack == null) return title;
            Control text = detail ? Ui.Column(2, title, Ui.Label(pack.PaidApi ? "Comfy credits" : "Local workflow", Palette.Secondary)) : title;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,8,*"), Margin = new Thickness(0, detail ? 3 : 0) };
            row.Children.Add(Icons.Create(Icons.Layers, 16, pack.PaidApi ? Palette.Accent : Palette.Secondary));
            Grid.SetColumn(text, 2); row.Children.Add(text);
            return row;
        }
        picker.ItemTemplate = new FuncDataTemplate<string>((name, _) => Row(name, true));
        picker.SelectionBoxItemTemplate = new FuncDataTemplate<string>((name, _) => Row(name, false));
        StyleModelField(picker, "Workflow model");
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
