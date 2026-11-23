using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class DockTabsTests
{
    [AvaloniaFact]
    public void Resources_open_separately_join_a_tab_strip_and_keep_the_active_tab_in_saved_layouts()
    {
        var app = Assert.IsType<App>(Application.Current);
        var language = Locale.Language;
        Locale.Language = "ru";
        var library = Path.Combine(Path.GetTempPath(), "composa-dock-preview-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(null, url => new Composa.App.AI.ComfyClient(url), library) { Width = 1440, Height = 900 };
        var document = EditorSession.NewCanvas(520, 320, new SKColor(235, 223, 201));
        try
        {
            app.ApplyTheme(EditorTheme.DarkNeutral);
            window.Show();
            window.AddSession(document);
            document.AddText(new(65, 90), new() { Text = "Каталог кофе", Size = 34 });
            window.SelectTool(Tool.Move);
            var state = document.Revision;
            Pump();
            var dock = window.GetVisualDescendants().OfType<SideDock>().Single();
            var layers = dock.Section("Layers");
            var resources = dock.Section("Resources");
            var properties = dock.Section("Properties");
            Assert.False(resources.State.Visible);

            ToggleResources();
            Assert.True(dock.Presents(resources));
            Assert.Equal(2, dock.Columns(false).Count());
            Assert.Same(resources.Content, dock.GetVisualDescendants().OfType<ResourceLibraryPanel>().Single());

            DragToHeader(resources, layers);
            Assert.Single(dock.Columns(false));
            Assert.NotNull(layers.State.TabGroup);
            Assert.Equal(layers.State.TabGroup, resources.State.TabGroup);
            Assert.True(dock.Presents(resources));
            Assert.False(dock.Presents(layers));
            Assert.Null(layers.Content.Parent);

            DragToHeader(properties, resources);
            Assert.Equal(layers.State.TabGroup, properties.State.TabGroup);
            Press(Tab(resources));
            Assert.True(dock.Presents(resources));
            Assert.False(dock.Presents(properties));
            Assert.Null(properties.Content.Parent);
            Assert.True(Tab(resources).Focus());
            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
            Pump();
            Assert.True(dock.Presents(properties));
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            Pump();
            Assert.True(dock.Presents(resources));
            AssertSelection();
            Assert.True(Screenshots.Save(window, "native-dock-tabs-dark"));
            app.ApplyTheme(EditorTheme.LightNeutral);
            window.Width = 1280;
            window.Height = 720;
            Pump();
            AssertSelection();
            Assert.True(Screenshots.Save(window, "native-dock-tabs-light-1280"));

            var saved = JsonSerializer.Deserialize<WorkspaceLayout>(JsonSerializer.Serialize(window.CaptureWorkspace()))!;
            var preferences = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(window.Settings))!;
            Assert.Equal(saved.Panels["Resources"], preferences.Dock["Resources"]);
            Assert.True(saved.Panels["Resources"].ActiveTab);
            Press(Tab(layers));
            Assert.True(dock.Presents(layers));
            Assert.False(dock.Presents(resources));
            ToggleResources();
            Assert.False(resources.State.Visible);
            Assert.DoesNotContain(window.GetVisualDescendants(), view => view is ResourceLibraryPanel);
            window.ApplyWorkspace(saved);
            Pump();
            Assert.True(dock.Presents(resources));
            AssertSelection();

            var column = dock.Columns(false).Single().View;
            var edge = At(column, column.Bounds.Width - 9, 140);
            Drag(Tab(resources), edge, cancel: true);
            Assert.Single(dock.Columns(false));
            Assert.Equal(layers.State.TabGroup, resources.State.TabGroup);
            Drag(Tab(resources), edge);
            Assert.Null(resources.State.TabGroup);
            Assert.Equal(layers.State.TabGroup, properties.State.TabGroup);
            Assert.Equal(2, dock.Columns(false).Count());
            Assert.True(dock.Presents(resources));
            Assert.Same(resources.Content, dock.GetVisualDescendants().OfType<ResourceLibraryPanel>().Single());
            Assert.Equal(state, document.Revision);
        }
        finally
        {
            document.MarkSaved("dock-tabs.cmps");
            window.Close();
            Locale.Language = language;
            app.ApplyTheme(EditorTheme.Classic);
        }

        Button Tab(DockSection section) => Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Tag == section);

        void ToggleResources()
        {
            var menu = window.GetLogicalDescendants().OfType<MenuItem>().Single(item => Equals(item.Header, Locale.T("Resources")));
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
        }

        void DragToHeader(DockSection source, DockSection target)
        {
            var group = Assert.IsType<DockGroup>(target.Frame);
            var header = group.Header;
            var point = At(header, Math.Min(165, header.Bounds.Width - 40), header.Bounds.Height / 2);
            Drag(Tab(source), point, tabTarget: true);
        }

        void Drag(Button source, Point end, bool tabTarget = false, bool cancel = false)
        {
            var start = At(source, source.Bounds.Width / 2, source.Bounds.Height / 2);
            window.MouseMove(start); window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start.WithY(start.Y + 9), RawInputModifiers.LeftMouseButton);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Pump();
            var preview = window.GetVisualDescendants().Single(item => item.Name == "DockDropTarget");
            if (tabTarget) Assert.InRange(preview.Bounds.Height, 39, 41);
            else Assert.InRange(preview.Bounds.Width, 20, 30);
            if (cancel) window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.MouseUp(end, MouseButton.Left);
            Pump();
            Assert.DoesNotContain(window.GetVisualDescendants(), item => item.Name == "DockDropTarget");
        }

        Point At(Control control, double x, double y) => control.TranslatePoint(new Point(x, y), window)!.Value;

        void Press(Button button)
        {
            var point = At(button, button.Bounds.Width / 2, button.Bounds.Height / 2);
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Pump();
        }

        void AssertSelection()
        {
            var tabs = window.GetVisualDescendants().OfType<Button>().Where(button => button.Tag is DockSection section
                && (section.Title is "Layers" or "Properties" or "Resources")).ToArray();
            Assert.Equal(3, tabs.Length);
            var labels = tabs.Select(button => Assert.IsType<TextBlock>(button.Content)).ToArray();
            Assert.Single(labels.Select(label => Math.Round(At(label, 0, 0).Y, 3)).Distinct());
            Assert.Single(labels.Select(label => label.FontSize).Distinct());
            Assert.All(tabs, button =>
            {
                Assert.Equal(default, button.BorderThickness);
                Assert.Equal(new CornerRadius(5), button.CornerRadius);
            });
            var current = Assert.Single(tabs, button => button.Classes.Contains("dock-selected"));
            Assert.Equal("Resources", Assert.IsType<DockSection>(current.Tag).Title);
            Assert.Same(Palette.ActiveTab, current.Background);
        }

        void Pump()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
