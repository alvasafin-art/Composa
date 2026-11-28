using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.AI;
using Composa.App.Controls;
using Composa.Editing;
using Composa.IO;
using Composa.Model;
using Composa.Painting;
using Composa.Rendering;
using Composa.Text;
using SkiaSharp;

namespace Composa.App.Tests;

public sealed class EditorWorkflowUiTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_align_move_enter_and_edit_a_card(bool autoLayout)
    {
        var s = EditorSession.NewCanvas(560, 360, new SKColor(245, 237, 223));
        var photoPixels = Pixels.NewColor(178, 94);
        var photoFile = Environment.GetEnvironmentVariable("COMPOSA_LAYOUT_PREVIEW_PHOTO");
        using (var c = new SKCanvas(photoPixels))
        {
            c.Clear(SKColors.Sienna);
            if (File.Exists(photoFile))
            {
                using var bitmap = SKBitmap.Decode(photoFile);
                c.DrawBitmap(bitmap, new SKRect(0, 0, 178, 94));
            }
        }
        var photo = s.AddImageLayer("Фото", photoPixels, new(170, 100), fit: false);
        var title = s.AddText(new(117, 175), new TextStyle { Text = "Горный кофе", FontFamily = "Arial", Size = 24 });
        var price = s.AddText(new(140, 224), new TextStyle { Text = "690 ₽", FontFamily = "Arial", Size = 23 });
        var language = Locale.Language;
        Locale.Language = "ru";
        var w = Open(s);
        try
        {
            w.SelectTool(Tool.Move);
            s.View = s.View with { Snap = false };
            w.Canvas.ZoomTo(1);
            s.SelectLayer(price.Id);
            s.SelectLayer(photo.Id, extend: true);
            s.SelectLayer(title.Id, extend: true);
            Pump();
            var initialPhoto = photo.ControlBounds;
            float left = photo.ControlBounds.Left;
            ClickButton(Find<Button>(w, "AlignLeft"));
            Assert.Equal(initialPhoto, photo.ControlBounds);
            Assert.Equal(left, title.ControlBounds.Left, 3);
            Assert.Equal(left, price.ControlBounds.Left, 3);

            var photoAt = At(w, photo.ControlBounds.MidX, photo.ControlBounds.MidY);
            Click(w, photoAt);
            Assert.Equal(photo.Id, s.AlignmentAnchor?.Id);
            Assert.Equal(3, s.Document.SelectedLayerIds.Count);
            Save(w, "workflow-alignment-anchor");

            photo = s.Document.Find(photo.Id)!;
            title = s.Document.Find(title.Id)!;
            price = s.Document.Find(price.Id)!;
            if (autoLayout) Assert.True(s.CreateAutoLayout(s.ProposeAutoLayout(out _)!));
            else s.GroupSelectedLayers();
            var group = s.ActiveLayer!;
            s.Rename(group, "Карточка");
            s.ClearLayerSelection();
            w.MouseMove(At(w, photo.ControlBounds.MidX, photo.ControlBounds.MidY));
            Save(w, autoLayout ? "workflow-hover-layout" : "workflow-hover-group");
            Click(w, At(w, photo.ControlBounds.MidX, photo.ControlBounds.MidY));
            Assert.Equal(group.Id, s.ActiveLayer?.Id);
            photo = s.Document.Find(photo.Id)!;
            price = s.Document.Find(price.Id)!;
            var oldPhoto = photo.ControlBounds;
            var at = At(w, photo.ControlBounds.MidX, photo.ControlBounds.MidY);
            w.MouseDown(at, MouseButton.Left);
            w.MouseMove(at + new Vector(34, 18), RawInputModifiers.LeftMouseButton);
            w.MouseUp(at + new Vector(34, 18), MouseButton.Left);
            Pump();
            Assert.Equal(oldPhoto.Left + 34, s.Document.Find(photo.Id)!.ControlBounds.Left, 1);
            Assert.Equal(oldPhoto.Top + 18, s.Document.Find(photo.Id)!.ControlBounds.Top, 1);

            Point AtPrice() => At(w, s.Document.Find(price.Id)!.ControlBounds.MidX, s.Document.Find(price.Id)!.ControlBounds.MidY);
            Click(w, AtPrice());
            Click(w, AtPrice());
            Assert.Equal(price.Id, s.ActiveLayer?.Id);
            Assert.False(s.IsEditingText);
            Save(w, autoLayout ? "workflow-inside-layout" : "workflow-inside-group");
            await Task.Delay(550, TestContext.Current.CancellationToken);
            Click(w, AtPrice());
            Click(w, AtPrice());
            Assert.True(s.IsEditingText);
            w.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            w.KeyTextInput("790 ₽");
            s.FinishText();
            Assert.Equal("790 ₽", s.Document.Find(price.Id)!.Text!.Text);
            w.SelectTool(Tool.Move);
            w.Canvas.Focus();
            w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Equal(group.Id, s.ActiveLayer?.Id);
        }
        finally { Close(w); Locale.Language = language; }
    }

    [AvaloniaFact]
    public async Task Nested_groups_enter_one_level_and_control_can_pick_a_child()
    {
        var s = EditorSession.NewCanvas(420, 280, SKColors.White);
        var child = s.AddText(new(158, 107), new TextStyle { Text = "Price", FontFamily = "Arial", Size = 28 });
        s.GroupSelectedLayers();
        var inner = s.ActiveLayer!;
        s.GroupSelectedLayers();
        var outer = s.ActiveLayer!;
        s.ClearLayerSelection();
        var w = Open(s);
        try
        {
            w.SelectTool(Tool.Move);
            var point = At(w, child.ControlBounds.MidX, child.ControlBounds.MidY);
            Click(w, point);
            Assert.Equal(outer.Id, s.ActiveLayer?.Id);
            Click(w, point);
            Assert.Equal(inner.Id, s.ActiveLayer?.Id);
            await Task.Delay(550, TestContext.Current.CancellationToken);
            Click(w, point);
            Click(w, point);
            Assert.Equal(child.Id, s.ActiveLayer?.Id);
            w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Equal(inner.Id, s.ActiveLayer?.Id);
            w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.Equal(outer.Id, s.ActiveLayer?.Id);
            Click(w, point, RawInputModifiers.Control);
            Assert.Equal(child.Id, s.ActiveLayer?.Id);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public async Task Deselecting_clears_group_guides_and_move_can_drag_another_group_from_its_pixels()
    {
        var s = EditorSession.NewCanvas(470, 300, SKColors.White);
        var title = s.AddText(new(48, 55), new TextStyle { Text = "Первая\nгруппа", FontFamily = "Arial", Size = 26 });
        s.GroupSelectedLayers();
        var firstGroup = s.ActiveLayer!;
        Layer Swatch(string name, SKColor color, SKPoint center)
        {
            var pixels = Pixels.NewColor(54, 44);
            pixels.Erase(color);
            return s.AddImageLayer(name, pixels, center, fit: false);
        }
        var first = Swatch("Синий", SKColors.SteelBlue, new(290, 125));
        var second = Swatch("Жёлтый", SKColors.Goldenrod, new(374, 167));
        s.SelectLayer(firstGroup.Id);
        var w = Open(s);
        try
        {
            s.View = s.View with { Snap = false };
            w.Canvas.ShowTransformControls = false;
            w.Canvas.ZoomTo(1);
            s.ClearLayerSelection();
            w.MouseMove(new(2, 2));
            Save(w, "workflow-group-before-entry");
            var titlePoint = At(w, title.ControlBounds.MidX, title.ControlBounds.MidY);
            Click(w, titlePoint); Click(w, titlePoint);
            Assert.Equal(title.Id, s.ActiveLayer?.Id);
            s.ClearLayerSelection();
            w.MouseMove(new(2, 2));
            Save(w, "workflow-group-unselected");
            using var before = SKBitmap.Decode(Path.Combine(Screenshots.Folder, "workflow-group-before-entry.png"));
            using var after = SKBitmap.Decode(Path.Combine(Screenshots.Folder, "workflow-group-unselected.png"));
            var topLeft = At(w, 5, 5);
            var bottomRight = At(w, 460, 290);
            for (int y = (int)topLeft.Y; y < (int)bottomRight.Y; y++)
                for (int x = (int)topLeft.X; x < (int)bottomRight.X; x++)
                    Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));

            await Task.Delay(550, TestContext.Current.CancellationToken);
            Click(w, titlePoint); Click(w, titlePoint);
            Assert.Equal(title.Id, s.ActiveLayer?.Id);
            s.SelectLayer(first.Id);
            s.SelectLayer(second.Id, extend: true);
            w.KeyPressQwerty(PhysicalKey.G, RawInputModifiers.Control);
            Pump();
            var group = s.ActiveLayer!;
            Assert.True(group.IsGroup);
            Assert.Equal(new[] { first.Id, second.Id }, group.Children.Select(child => child.Id));
            var firstPosition = s.Document.Find(first.Id)!.ControlBounds;
            var secondPosition = s.Document.Find(second.Id)!.ControlBounds;
            var at = At(w, firstPosition.MidX, firstPosition.MidY);
            var destination = at + new Vector(24, 18);
            w.MouseDown(at, MouseButton.Left);
            w.MouseMove(destination, RawInputModifiers.LeftMouseButton);
            w.MouseUp(destination, MouseButton.Left);
            Pump();
            Assert.True(s.ActiveLayer?.Id == group.Id, $"Active: {s.ActiveLayer?.Name}, tool: {s.Tool}, history: {s.History.UndoName}");
            Assert.Equal(firstPosition.Left + 24, s.Document.Find(first.Id)!.ControlBounds.Left, 1);
            Assert.Equal(secondPosition.Top + 18, s.Document.Find(second.Id)!.ControlBounds.Top, 1);
            Save(w, "workflow-group-moved");
        }
        finally { Close(w); }
    }

    [AvaloniaTheory]
    [InlineData(Tool.CloneStamp)]
    [InlineData(Tool.HealingBrush)]
    [InlineData(Tool.SpotHealing)]
    public void Retouch_remembers_visible_sampling_after_the_selection_toggle_is_changed(Tool tool)
    {
        var color = new SKColor(130, 150, 175);
        var s = EditorSession.NewCanvas(256, 170, color);
        var original = s.ActiveLayer!;
        s.SelectRect(new SKRect(118, 76, 135, 93)); s.Fill(SKColors.Black); s.Deselect();
        var donor = original.Pixels!;
        var blank = s.AddBlankLayer();
        s.Rename(blank, "Retouch");
        s.SampleAllLayers = true;
        var w = Open(s);
        try
        {
            w.SelectTool(tool);
            ToggleSample(w, true);
            w.SelectTool(Tool.Wand);
            ToggleSample(w, false);
            w.SelectTool(tool);
            Assert.True(s.RetouchSampleAllLayers);
            Assert.False(s.SampleAllLayers);
            s.Brush = new() { Size = 33, Hardness = 1 };
            if (tool != Tool.SpotHealing) s.SetCloneSource(new(49, 61));
            var point = At(w, 126, 84);
            w.MouseDown(point, MouseButton.Left); w.MouseUp(point, MouseButton.Left); Pump();
            Assert.Same(donor, original.Pixels);
            Assert.True(blank.Pixels!.GetPixel(126,84).Alpha > 0);
            Assert.Equal(0, blank.Pixels.GetPixel(10,10).Alpha);
            if (tool is Tool.CloneStamp or Tool.SpotHealing) Assert.Equal(color, blank.Pixels.GetPixel(126,84));
            Save(w, "workflow-retouch-" + tool);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Brush_options_and_the_engine_accept_a_1000_pixel_diameter()
    {
        var s = EditorSession.NewCanvas(1200, 1200);
        var w = Open(s);
        try
        {
            w.SelectTool(Tool.Brush);
            Pump();
            var size = w.GetVisualDescendants().OfType<SliderField>().Single(field => field.IsEffectivelyVisible && field.Label == "Size");
            Assert.Equal(1000, size.Maximum);
            var at = size.TranslatePoint(new Rect(size.Bounds.Size).Center, w)!.Value;
            Click(w, at); Click(w, at);
            Assert.True(size.IsEditing);
            w.KeyTextInput("990");
            w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal(990, s.Brush.Size);
            w.Canvas.Focus();
            w.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
            Assert.Equal(1000, size.Value);
            Assert.Equal(1000, s.Brush.Size);
            s.Brush = s.Brush with { Hardness = 1, FullCursor = true };
            Assert.Equal(500, s.Brush.CursorRadius());
            Assert.True(s.BeginStroke(new(600,600), out var problem), problem);
            s.EndStroke();
            var footprint = Pixels.ContentBounds(s.ActiveLayer!.Pixels!);
            Assert.InRange(footprint.Width, 997, 1002);
            Assert.InRange(footprint.Height, 997, 1002);
            w.MouseMove(At(w, 600, 600));
            Save(w, "workflow-brush-1000");
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public void Text_frame_tightens_without_mutating_placement_or_hit_coverage()
    {
        var s = EditorSession.NewCanvas(520, 280, new SKColor(248,244,234));
        var point = s.AddText(new(100,55), new TextStyle { Text = "Й Ё р у", FontFamily = "Arial", Size = 32 });
        var paragraph = s.AddText(new(100,133), point.Text! with {
            Text = "Й Ё р у пьют чай\nУтром в саду", Italic = true, BoxWidth = 278, BoxHeight = 140, AutoBoxHeight = true });
        var w = Open(s);
        try
        {
            w.SelectTool(Tool.Move); w.Canvas.ZoomTo(1);
            Assert.InRange(point.Bounds.Width - point.ControlBounds.Width, 15, 50);
            Assert.Equal(paragraph.Bounds.Width, paragraph.ControlBounds.Width);
            var layout = new TextLayout(paragraph.Text!);
            Assert.Equal(3, layout.Lines.Count);
            var baseline = layout.Lines[0].Baseline;
            s.SelectLayer(paragraph.Id); Save(w, "workflow-text-paragraph");
            s.ClearLayerSelection();
            Pump();
            var original = point.Transform;
            Click(w, At(w, point.Bounds.Left + 3, point.Bounds.MidY));
            Assert.Equal(point.Id, s.ActiveLayer?.Id);
            Assert.Equal(original, s.Document.Find(point.Id)!.Transform);
            Assert.Equal(baseline, new TextLayout(paragraph.Text!).Lines[0].Baseline);
            Save(w, "workflow-text-point");
        }
        finally { Close(w); }
    }

    [AvaloniaTheory]
    [InlineData("ru")]
    [InlineData("en")]
    public async Task Models_and_inline_progress_render_at_narrow_width(string currentLanguage)
    {
        var language = Locale.Language; Locale.Language = currentLanguage;
        var suffix = currentLanguage == "ru" ? "" : "-en";
        var s = EditorSession.NewCanvas(540, 360, SKColors.White);
        var w = new MainWindow { Width = 900, Height = 760 };
        w.Settings.CheckForUpdates = false;
        w.Settings.ImageGenerationCodexPath = Path.Combine(Path.GetTempPath(),"no-codex-fixture.exe");
        w.Settings.ImageGenerationApiUrl = "invalid";
        w.Settings.AiTaskEngineIds[AiTaskKind.GenerativeFill.ToString()] = "chatgpt-image-2.5";
        w.AiTasks.ApiKey = () => "fixture";
        w.Show(); w.AddSession(s); s.SelectRect(new(160,100,290,175));
        try
        {
            var dock = w.GetVisualDescendants().OfType<SideDock>().Single();
            dock.SetVisible(dock.Section("Properties"),false);
            dock.SetVisible(dock.Section("History"),false);
            var section = dock.Section("AI Generation");
            var layout = dock.CaptureLayout().ToDictionary();
            layout[section.Title] = layout[section.Title] with { Height = 244 };
            dock.RestoreLayout(layout, persist:false);
            dock.SetVisible(section,true); Pump();
            var connect = Find<Button>(section.Content,"ConnectAiModels");
            Assert.True(connect.IsEffectivelyVisible);
            Assert.False(Find<Button>(section.Content,"AiAdvanced").IsEffectivelyEnabled);
            Save(w,"workflow-ai-no-models" + suffix);
            w.AiTasks.SetConnectedForTests(PartnerImageTests.Capabilities());
            Pump();
            var picker = section.Content.GetVisualDescendants().OfType<ComboBox>().Single();
            Assert.All(picker.Items.OfType<ImageModelChoice>(), item => Assert.True(item.Available));
            Assert.Equal("chatgpt-image-2.5", Assert.IsType<ImageModelChoice>(picker.SelectedItem).Engine.Id);
            Assert.False(connect.IsEffectivelyVisible);
            var popup = Assert.Single(picker.GetVisualDescendants().OfType<Popup>());
            popup.ShouldUseOverlayLayer = true;
            picker.IsDropDownOpen = true;
            Save(w,"workflow-ai-models" + suffix);
            Assert.InRange(Math.Abs(popup.Child!.TranslatePoint(default,w)!.Value.X - picker.TranslatePoint(default,w)!.Value.X), 0, 1);
            picker.IsDropDownOpen = false;
            var operation = Find<Button>(section.Content,"AiDockOperation");
            var operations = Assert.IsType<MenuFlyout>(operation.Flyout);
            operations.ShowAt(operation); Pump();
            Assert.Contains(operations.Items.OfType<MenuItem>(),item => !item.IsEnabled);
            Assert.Contains(operations.Items.OfType<MenuItem>(),item => item.IsEnabled);
            Save(w,"workflow-ai-operations" + suffix);
            operations.Hide();
            var prompt = section.Content.GetVisualDescendants().OfType<TextBox>().Single(item => item.AcceptsReturn);
            prompt.Text = "Поставь рядом керамическую чашку.";
            var tail = Find<Button>(section.Content,"AiRunVariants");
            var run = Find<Button>(section.Content,"AiRun");
            var flyout = Assert.IsAssignableFrom<MenuFlyout>(tail.Flyout);
            flyout.ShowAt(tail); Save(w,"workflow-ai-variants" + suffix);
            Assert.Equal(new[] { Locale.T("2 variants"), Locale.T("3 variants") }, flyout.Items.OfType<MenuItem>().Select(item => item.Header));
            var variants = Assert.Single(w.GetVisualDescendants().OfType<MenuFlyoutPresenter>());
            var tailOrigin = tail.TranslatePoint(default,w)!.Value;
            var menuOrigin = variants.TranslatePoint(default,w)!.Value;
            Assert.InRange(Math.Abs(tailOrigin.X + tail.Bounds.Width - menuOrigin.X - variants.Bounds.Width), 0, 1);
            Assert.InRange(Math.Abs(tailOrigin.Y + tail.Bounds.Height - menuOrigin.Y), 0, 1);
            flyout.Hide();
            Save(w,"workflow-ai-ready" + suffix);
            AssertCaption(currentLanguage == "ru" ? "Старт" : "Run");
            void AssertCaption(string expected)
            {
                var caption = run.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == expected);
                var origin = caption.TranslatePoint(default, run)!.Value;
                Assert.InRange(origin.X, 0, run.Bounds.Width);
                Assert.InRange(origin.X + caption.Bounds.Width, 0, run.Bounds.Width);
                var clip = Assert.IsType<Grid>(run.Content).Clip!;
                Assert.False(clip.FillContains(new Point(.25, .25)));
                Assert.False(clip.FillContains(new Point(.25, run.Bounds.Height - .25)));
                Assert.True(clip.FillContains(new Point(run.Bounds.Width - .25, .25)));
            }
            async Task State(string filename, AiOperationState state)
            {
                typeof(AiTaskService).GetProperty(nameof(AiTaskService.Operation))!.SetValue(w.AiTasks,state);
                typeof(MainWindow).GetMethod("RefreshAiUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w,null);
                await Task.Delay(300, TestContext.Current.CancellationToken);
                Save(w, filename + suffix);
            }
            await State("workflow-ai-queue",new() { Status = AiOperationStatus.Queued });
            AssertCaption(Locale.T("Queued"));
            await State("workflow-ai-waiting",new() { Status = AiOperationStatus.Running, Stage = "Waiting for the model", ElapsedSeconds = 17 });
            AssertCaption("0:17");
            await State("workflow-ai-progress",new() { Status = AiOperationStatus.Running, Value = 13, Maximum = 24, ElapsedSeconds = 17 });
            AssertCaption("0:17 · 54%");
            s.AddPromptRegion(new(20,30,100,100));
            s.AddPromptRegion(new(120,30,200,100));
            s.AddPromptRegion(new(220,30,300,100));
            await State("workflow-ai-progress-long",new() { Status = AiOperationStatus.Running, Value = 13, Maximum = 24, ElapsedSeconds = 48297 });
            AssertCaption("13:24:57 · 54%");
            var regionButton = Find<Button>(section.Content, "DeleteAllPromptRegions");
            Assert.False(regionButton.IsEffectivelyEnabled);
            var regionCaptions = section.Content.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.Text == Locale.T("Region prompt")).ToArray();
            Assert.Equal(3, regionCaptions.Length);
            Assert.All(regionCaptions, text => Assert.Equal(.48, text.Opacity));
            var surface = Assert.Single(regionButton.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>());
            Assert.Equal(Avalonia.Media.Colors.Transparent, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(surface.Background).Color);
            Assert.Null(tail.Flyout);
            Assert.False(picker.IsEffectivelyEnabled);
            Assert.InRange(picker.Opacity, 0.5, 0.7);
            await State("workflow-ai-applying",new() { Status = AiOperationStatus.Running, Stage = "Applying result", ElapsedSeconds = 30 });
            AssertCaption(Locale.T("Inserting"));
            await State("workflow-ai-done",new() { Status = AiOperationStatus.Completed, CreditsUsed = 10.86, ElapsedSeconds = 31 });
            AssertCaption($"{Locale.T("Done")} · 0:31");
            Assert.All(regionCaptions, text => Assert.Equal(1, text.Opacity));
            Assert.False(tail.IsEffectivelyEnabled);
            await Task.Delay(890, TestContext.Current.CancellationToken);
            Save(w,"workflow-ai-charge" + suffix);
            await State("workflow-ai-error",new() { Status = AiOperationStatus.Failed, Error = "Сервер не ответил" });
            await State("workflow-ai-cancel",new() { Status = AiOperationStatus.Cancelled });
            w.AiTasks.AcceptSettingsPreview(w.AiTasks.SettingsPreview(),"http://127.0.0.1:8");
            Pump();
            Assert.Null(picker.SelectedItem);
        }
        finally { Close(w); Locale.Language = language; }
    }

    [AvaloniaFact]
    public void A_new_prompt_box_is_visible_even_when_other_frames_are_hidden()
    {
        var first = EditorSession.NewCanvas(460,300,SKColors.White);
        var w = Open(first);
        try
        {
            var dock = w.GetVisualDescendants().OfType<SideDock>().Single();
            dock.SetVisible(dock.Section("AI Generation"), true);
            w.SelectTool(Tool.PromptRegion);
            first.AddPromptRegion(new(20,20,70,70));
            w.Canvas.ShowPromptRegions = false;
            Pump();
            var start = At(w,110,82);
            var end = At(w,245,168);
            w.MouseDown(start,MouseButton.Left);
            w.MouseMove(end,RawInputModifiers.LeftMouseButton);
            w.MouseUp(end,MouseButton.Left);
            Pump();
            Assert.Equal(2,first.Document.PromptRegions.Count);
            Assert.Equal(2,first.RevealedPromptRegion);
            Save(w,"workflow-bbox-new-visible");
            var second = EditorSession.NewCanvas(460,300,SKColors.White);
            w.AddSession(second);
            Assert.True(w.Canvas.ShowPromptRegions);
            Assert.False(first.PromptRegionsVisible);
        }
        finally { Close(w); }
    }

    [AvaloniaFact]
    public async Task Resources_save_from_a_layer_drop_and_keep_groups_and_cursor_placement()
    {
        var path = Directory.CreateTempSubdirectory("composa-library-workflow-").FullName;
        var library = new ElementLibrary(path);
        var s = EditorSession.NewCanvas(520,320,SKColors.White);
        var text = s.AddText(new(95,80),new TextStyle { Text = "Карточка", FontFamily = "Arial", Size = 22 });
        var language = Locale.Language;
        Locale.Language = "ru";
        var w = new MainWindow(null,url => new ComfyClient(url),path) { Width = 1280, Height = 750 };
        w.Settings.CheckForUpdates = false;
        w.Show(); w.AddSession(s);
        try
        {
            var dock = w.GetVisualDescendants().OfType<SideDock>().Single();
            var section = dock.Section("Resources");
            dock.SetVisible(section,true);
            var panel = (ResourceLibraryPanel)section.Content;
            await panel.Refresh();
            Save(w,"workflow-library-empty");
            Assert.Equal(Locale.T("Open Library Folder"),AutomationProperties.GetName(Find<Button>(panel,"OpenLibraryFolder")));
            Assert.DoesNotContain(panel.GetVisualDescendants().OfType<Button>(),button => button.Name == "SaveLibraryElement");
            var layerPanel = w.GetVisualDescendants().OfType<LayersPanel>().Single();
            typeof(LayersPanel).GetField("externalReferenceDrag",BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(layerPanel,(s,text.Id));
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(LayersPanel.LibrarySelectionDragFormat,text.Id.ToString("N")));
            var panelPoint = new Point(95,100);
            void Drag(Control target, RoutedEvent<DragEventArgs> routed, IDataTransfer transferable, Point point)
                => target.RaiseEvent(new DragEventArgs(routed,transferable,target,point,KeyModifiers.None)
                    { DragEffects = DragDropEffects.Copy | DragDropEffects.Move });
            Drag(panel,DragDrop.DragOverEvent,data,panelPoint);
            Save(w,"workflow-library-save-drop");
            Drag(panel,DragDrop.DropEvent,data,panelPoint);
            await Wait(() => Tiles().Length == 1);
            Drag(panel,DragDrop.DropEvent,data,panelPoint);
            await Wait(() => Tiles().Length == 2);
            Button[] Tiles() => panel.GetVisualDescendants().OfType<Button>().Where(button => button.Name == "LibraryResource").ToArray();
            Point Center(Button button) => button.TranslatePoint(new Rect(button.Bounds.Size).Center,w)!.Value;
            Click(w,Center(Tiles()[0]));
            w.Canvas.Focus(); // A panel rebuild can return keyboard focus to the canvas.
            w.KeyPressQwerty(PhysicalKey.G,RawInputModifiers.Control);
            var layerCount = s.Document.Layers.Count;
            w.KeyPressQwerty(PhysicalKey.J,RawInputModifiers.Control);
            Assert.Equal(layerCount, s.Document.Layers.Count);
            Assert.Equal(text.Id, s.ActiveLayer?.Id);
            Assert.Empty(library.ReadOrganization().Groups);
            Click(w,Center(Tiles()[1]),RawInputModifiers.Control);
            w.Canvas.Focus();
            w.KeyPressQwerty(PhysicalKey.G,RawInputModifiers.Control);
            await Wait(() => library.ReadOrganization().Groups.Length == 1);
            await Wait(() => panel.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "LibraryGroup"));
            Save(w,"workflow-library-expanded");
            var group = Find<Button>(panel,"LibraryGroup");
            Click(w,Center(group));
            await Wait(() => library.ReadOrganization().Groups[0].Collapsed);
            await Wait(() => Tiles().Length == 0);
            Save(w,"workflow-library-collapsed");
            w.Canvas.Focus();
            w.KeyPressQwerty(PhysicalKey.G,RawInputModifiers.Control | RawInputModifiers.Shift);
            await Wait(() => library.ReadOrganization().Groups.Length == 0 && Tiles().Length == 2);
            var before = library.ReadOrganization().Order;
            var dragEntry = ResourceLibraryPanel.DragData(before[1]);
            Drag(Tiles()[0],DragDrop.DropEvent,dragEntry,new Point(8,2));
            await Wait(() => library.ReadOrganization().Order.FirstOrDefault() == before[1]);
            var drop = w.Canvas.ToScreen(new SKPoint(387,232));
            Drag(w.Canvas,DragDrop.DragOverEvent,dragEntry,drop);
            Save(w,"workflow-library-placement");
            Drag(w.Canvas,DragDrop.DropEvent,dragEntry,drop);
            await Wait(() => s.ActiveLayer?.Id != text.Id);
            var frame = EditorSession.LayoutBounds(s.ActiveLayer!);
            Assert.InRange(frame.MidX,386.5f,387.5f);
            Assert.InRange(frame.MidY,231.5f,232.5f);
            Save(w,"workflow-library-inserted");
        }
        finally
        {
            Close(w); Locale.Language = language;
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static async Task Wait(Func<bool> ready)
    {
        for (int attempt = 0; attempt < 140 && !ready(); attempt++)
        {
            Pump();
            await Task.Delay(15,TestContext.Current.CancellationToken);
        }
        Pump();
        Assert.True(ready());
    }

    private static void ToggleSample(MainWindow window, bool value)
    {
        var check = window.GetVisualDescendants().OfType<CheckBox>().Single(box => box.IsEffectivelyVisible &&
            Equals(box.Content, Locale.T("Sample all layers")));
        check.IsChecked = value;
        Pump();
    }
    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static MainWindow Open(EditorSession s)
    {
        var w = new MainWindow { Width = 1100, Height = 780 };
        w.Settings.CheckForUpdates = false;
        w.Show(); w.AddSession(s); Pump();
        return w;
    }
    private static void Close(MainWindow w)
    {
        foreach (var session in w.Sessions) session.MarkSaved("workflow.cmps");
        w.Close();
    }
    private static Point At(MainWindow w, float x, float y) =>
        w.Canvas.TranslatePoint(w.Canvas.ToScreen(new SKPoint(x,y)),w)!.Value;
    private static void Click(MainWindow w, Point point, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.MouseDown(point,MouseButton.Left,modifiers);
        w.MouseUp(point,MouseButton.Left,modifiers);
        Pump();
    }
    private static void ClickButton(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Save(MainWindow w, string name) { Pump(); Assert.True(Screenshots.Save(w,name)); }
}
