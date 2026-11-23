using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class SharedStylesUiTests
{
    [AvaloniaFact]
    public void Shared_text_style_menu_discloses_scope_saves_edits_and_reverts_one_field()
    {
        var app = Assert.IsType<App>(Application.Current);
        Locale.Language = "ru";
        var window = new MainWindow { Width = 1280, Height = 720 };
        var s = EditorSession.NewCanvas(640, 420, new SKColor(244, 234, 221));
        try
        {
            app.ApplyTheme(EditorTheme.DarkNeutral);
            window.Show();
            window.AddSession(s);
            var first = s.AddText(new SKPoint(127, 156), new TextStyle { Text = "690 ₽", Size = 31, Color = 0xff523824 });
            var second = s.AddText(new SKPoint(350, 156), new TextStyle { Text = "750 ₽", Size = 31, Color = 0xff523824 });
            window.SelectTool(Tool.Move);
            s.SelectLayer(first.Id);
            Pump();

            var actions = Find<Button>(window, "SharedTextStyleActions");
            Menu(actions, "Save as Style…");
            var save = Assert.Single(window.OwnedWindows);
            Find<TextBox>(save, "NewLayerStyleName").Text = "Цена";
            Accept(save);
            var style = Assert.Single(s.Document.Styles.Values);
            Assert.Equal("Цена", style.Name);

            s.SelectLayer(second.Id);
            Pump();
            var picker = Find<ComboBox>(window, "SharedTextStylePicker");
            Assert.Equal(2, picker.Items.Count);
            picker.SelectedIndex = 1;
            Assert.Equal(style.Id, second.SharedTextStyle!.Id);
            s.SetTextColor(0xff184e99);
            Assert.True(Find<TextBlock>(window, "TextStyleOverrides").IsVisible);
            Assert.True(Screenshots.Save(window, "shared-text-style-1280"));

            actions = Find<Button>(window, "SharedTextStyleActions");
            Menu(actions, "Edit Style…");
            var editor = Assert.Single(window.OwnedWindows);
            Assert.Contains("2", Find<TextBlock>(editor, "SharedStyleLinkedCount").Text);
            var size = Find<NumericUpDown>(editor, "SharedStyleFontSize");
            size.Value = 42;
            Assert.Equal(31, first.Text!.Size);
            Assert.True(Screenshots.Save(editor, "shared-style-edit-scope"));
            Accept(editor);
            Assert.Equal(42, first.Text.Size);
            Assert.Equal(42, second.Text!.Size);
            Assert.Equal(0xff184e99u, second.Text.Color);
            Assert.Equal(0xff523824u, first.Text.Color);
            Assert.True(Screenshots.Save(window, "shared-text-style-applied-1280"));

            actions = Find<Button>(window, "SharedTextStyleActions");
            var menu = Open(actions);
            var reset = FindMenuItem(menu.Items, "Revert to Style Value");
            Click(FindMenuItem(reset.Items, "Text Color"));
            menu.Close();
            Pump();
            Assert.Equal(0xff523824u, second.Text.Color);
            Assert.False(Find<TextBlock>(window, "TextStyleOverrides").IsVisible);
            window.Width = 1920;
            window.Height = 1080;
            window.UpdateLayout();
            Assert.True(Screenshots.Save(window, "shared-text-style-1920"));
        }
        finally
        {
            Locale.Language = "en";
            app.ApplyTheme(EditorTheme.Classic);
            s.MarkSaved("shared-text-style.cmps", s.History.CurrentId);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Shape_appearance_dialog_exposes_shared_stroke_and_indicates_local_appearance()
    {
        var app = Assert.IsType<App>(Application.Current);
        Locale.Language = "ru";
        var window = new MainWindow { Width = 1280, Height = 720 };
        var s = EditorSession.NewCanvas(640, 420, new SKColor(243, 238, 231));
        try
        {
            app.ApplyTheme(EditorTheme.LightNeutral);
            window.Show();
            window.AddSession(s);
            var shape = new ShapeStyle(ShapeKind.RoundedRectangle, 0xffb99770, 10) { Stroke = 0xff674b36 };
            var first = s.AddShape(shape, SKRect.Create(128, 145, 156, 103))!;
            var second = s.AddShape(shape, SKRect.Create(353, 145, 156, 103))!;
            var style = s.CreateLayerStyle(first, LayerStyleKind.Appearance, "Обводка")!;
            s.ApplyLayerStyle(second, style.Id);
            s.SelectLayer(second.Id);
            window.SelectTool(Tool.Move);
            s.ChangeShapeStyle(second, second.Shape! with { Stroke = 0xff205d46 });
            Assert.True(Find<TextBlock>(window, "AppearanceStyleOverrides").IsVisible);
            Assert.True(Screenshots.Save(window, "shared-appearance-style-1280"));
            var actions = Find<Button>(window, "SharedAppearanceStyleActions");
            Menu(actions, "Edit Style…");
            var editor = Assert.Single(window.OwnedWindows);
            Assert.Contains("2", Find<TextBlock>(editor, "SharedStyleLinkedCount").Text);
            Find<NumericUpDown>(editor, "SharedStyleStrokeWidth").Value = 12;
            Accept(editor);
            Assert.Equal(12, first.Shape!.StrokeWidth);
            Assert.Equal(12, second.Shape!.StrokeWidth);
            Assert.Equal(0xff205d46u, second.Shape.Stroke);
        }
        finally
        {
            Locale.Language = "en";
            app.ApplyTheme(EditorTheme.Classic);
            s.MarkSaved("shared-appearance-style.cmps", s.History.CurrentId);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Text_style_can_be_deleted_from_the_menu_while_linked_or_unused()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        var session = EditorSession.NewCanvas(400, 240);
        try
        {
            window.Show();
            window.AddSession(session);
            var first = session.AddText(new(30, 30), new TextStyle { Text = "One", Size = 30 });
            var style = session.CreateLayerStyle(first, LayerStyleKind.Text, "Title")!;
            var second = session.AddText(new(30, 120), new TextStyle { Text = "Two", Size = 24 });
            Assert.True(session.ApplyLayerStyle(second, style.Id));
            session.SetTextColor(0xff275389);
            var unused = session.CreateLayerStyle(second, LayerStyleKind.Text, "Draft Title")!;
            Assert.True(session.ApplyLayerStyle(second, style.Id));
            session.SetTextColor(0xff275389);
            var color = second.Text!.Color;
            Pump();
            Button Actions() => Find<Button>(window, "SharedTextStyleActions");

            Menu(Actions(), "Delete Style…");
            var dialog = Assert.Single(window.OwnedWindows);
            Click(Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => button.IsCancel));
            Pump();
            Assert.Equal(2, session.Document.Styles.Count);
            Menu(Actions(), "Delete Style…");
            Accept(Assert.Single(window.OwnedWindows));
            Assert.Equal(unused, Assert.Single(session.Document.Styles.Values));
            Assert.Null(first.SharedTextStyle);
            Assert.Null(second.SharedTextStyle);
            Assert.Equal(color, second.Text.Color);
            Assert.Equal(2, Find<ComboBox>(window, "SharedTextStylePicker").Items.Count);

            var menu = Open(Actions());
            var saved = FindMenuItem(menu.Items, "Delete Saved Style");
            Click(Assert.Single(saved.Items.OfType<MenuItem>()));
            menu.Close();
            Pump();
            Accept(Assert.Single(window.OwnedWindows));
            Assert.Empty(session.Document.Styles);
            Assert.Single(Find<ComboBox>(window, "SharedTextStylePicker").Items);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            session.MarkSaved("delete-text-style.cmps");
            window.Close();
        }
    }

    private static ContextMenu Open(Button actions)
    {
        Click(actions);
        Pump();
        return Assert.IsType<ContextMenu>(actions.ContextMenu);
    }

    private static void Menu(Button actions, string command)
    {
        var menu = Open(actions);
        Click(FindMenuItem(menu.Items, command));
        menu.Close();
        Pump();
    }

    private static MenuItem FindMenuItem(IEnumerable<object> items, string label) =>
        Assert.Single(items.OfType<MenuItem>(), item => Equals(item.Header, Locale.T(label)));

    private static T Find<T>(Window window, string name) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), item => item.Name == name);

    private static void Accept(Window dialog)
    {
        Pump();
        var button = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), item => item.IsDefault);
        Assert.True(button.IsEnabled);
        Click(button);
        Pump();
    }

    private static void Click(Control control) => control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : Button.ClickEvent));
    private static void Pump() => Dispatcher.UIThread.RunJobs();
}
