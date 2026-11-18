using Composa.App.AI;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.AI;
using Composa.App.Dialogs;

namespace Composa.App.Tests;

public class AiOperationSettingsTests
{
    [AvaloniaFact]
    public async Task Fill_size_tooltip_reports_the_request_in_both_modes_without_a_permanent_output_section()
    {
        var window=new MainWindow(); window.Settings.CheckForUpdates=false; window.Show();
        try
        {
            window.Settings.SetOperation(Flux,AiTaskKind.GenerativeFill,window.Settings.OperationFor(Flux,AiTaskKind.GenerativeFill)
                with { OriginalSize=false,Megapixels=0.5,MaskGrow=4,MaskBlend=8,MaskBlur=4,MaskContext=1.2 });
            var before=JsonSerializer.Serialize(window.Settings);
            var pending=AiDialogs.Prompt(window,AiTaskKind.GenerativeFill,window.Settings,264,176,"Replace the plant with a ceramic vase",service:window.AiTasks,
                hasSelection:true,sourceCanvas:new(0,0,1537,991),selectionBounds:new(600,350,864,526));
            Dispatcher.UIThread.RunJobs(); var dialog=Assert.Single(window.OwnedWindows);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<Expander>(), e => Equals(e.Header, "Output details"));
            var size=Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),combo=>combo.Items.OfType<string>().Contains("Original size"));
            Assert.Contains("816 × 624 px", Assert.IsType<string>(ToolTip.GetTip(size)));
            size.SelectedItem="Original size"; Dispatcher.UIThread.RunJobs();
            Assert.Contains("1168 × 880 px", Assert.IsType<string>(ToolTip.GetTip(size)));
            size.SelectedItem=AiDimensions.Label(0.5); Dispatcher.UIThread.RunJobs();
            Assert.True(Screenshots.Save(dialog,"flux-selection-resolution-and-context"));
            dialog.Close(false); Assert.Null(await pending); Assert.Equal(before,JsonSerializer.Serialize(window.Settings));
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Named_preset_is_scoped_to_the_model_and_operation_and_committed_on_accept(bool accept)
    {
        var owner = new MainWindow();
        owner.Show();
        try
        {
            var pack = owner.AiTasks.Engines.Find(Flux)!;
            owner.AiTasks.SelectedEngine = pack;
            var settings = new Settings();
            var operation = AiTaskKind.GenerativeFill;
            var pending = AiDialogs.Advanced(owner, settings, owner.AiTasks, operation, new AiOperationSettings { Seed = 41 });
            var dialog = Assert.Single(owner.OwnedWindows);
            dialog.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Presets")).IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            dialog.GetVisualDescendants().OfType<CheckBox>().Single(b => Equals(b.Content, "Align pixels")).IsChecked = false;
            dialog.GetVisualDescendants().OfType<AiVariantButtons>().Single().Count = 2;
            // Click the actual control so its change is also propagated to the draft.
            var triple = dialog.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b => b.Name == "AiVariants3");
            triple.IsChecked = true;
            dialog.GetVisualDescendants().OfType<TextBox>().Single(b => b.PlaceholderText == "Preset name").Text = "Proof";
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save preset"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(settings.GenerationPresets);
            dialog.Close(accept);
            Assert.Equal(accept, await pending);
            if (accept)
            {
                var key = owner.AiTasks.Engines.ModelFor(pack, operation, settings.ComfyModelsFor(settings.ComfyServerUrl)).ModelKey;
                var saved = settings.GenerationPresets[$"{key}/{operation}"]["Proof"];
                Assert.Equal(41, saved.Seed);
                Assert.False(saved.AlignPixels);
                Assert.Equal(3, saved.Variants);
                Assert.False(settings.AiAlignMaskedEdits);
            }
            else Assert.Empty(settings.GenerationPresets);
        }
        finally { owner.Close(); }
    }

    [Fact]
    public void Model_picker_shows_the_configured_checkpoint_and_keeps_its_workflow_route()
    {
        var catalog = new EngineCatalog(Path.Combine(AppContext.BaseDirectory, "ai", "engines"));
        var engine = catalog.Find(Flux)!;
        var task = AiTaskKind.GenerativeFill;
        var graph = catalog.ReadWorkflow(engine, engine.Workflow(engine.Binding(task)!.Workflow));
        var slot = WorkflowModels.Slots(graph, engine.Id).First(s => s.Kind == EngineAssetKind.DiffusionModel);
        var choices = new Dictionary<string, string> { [slot.Key] = "project/retouch-v2.safetensors" };
        var model = catalog.ImageModels(task, engine, choices).Single(m => m.Engine.Id == Flux);
        Assert.Equal("retouch-v2", model.Name);
        Assert.Equal(choices[slot.Key], model.Identifier);
        Assert.Same(engine, model.Engine);
    }

    [Fact]
    public void Operation_readiness_only_requires_the_nodes_in_the_executed_graph()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ai", "engines");
        var service = new AiTaskService(() => "http://localhost:8188", root);
        var pack = service.Engines.Find(QwenImageWorkflow.EngineId)!;
        var nodes = pack.RequiredNodeTypes.ToHashSet();
        nodes.Remove("PixelDriftFix");
        var capabilities = new ComfyServerCapabilities { Version = "0.43.0", NodeTypes = nodes };
        service.SetConnectedForTests(capabilities);
        var profile = new AiOperationSettings();
        var edit = service.OperationCompatibility(pack, AiTaskKind.GenerativeFill, new Dictionary<string,string>(), profile);
        Assert.True(edit.IsCompatible, string.Join(", ", edit.Missing));
        nodes.Remove("SetLatentNoiseMask");
        edit = service.OperationCompatibility(pack, AiTaskKind.GenerativeFill, new Dictionary<string,string>(), profile);
        Assert.Contains("node SetLatentNoiseMask", edit.Missing);
        Assert.True(service.OperationCompatibility(pack, AiTaskKind.GenerateImage, new Dictionary<string,string>(), profile).IsCompatible);
    }

    [Fact]
    public void A_saved_model_cannot_silently_switch_to_another_checkpoint_on_the_same_route()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ai", "engines");
        var service = new AiTaskService(() => "http://localhost:8188", root);
        var pack = service.Engines.Find(Flux)!;
        var task = AiTaskKind.GenerativeFill;
        var slot = WorkflowModels.Slots(service.Engines.ReadWorkflow(pack, EngineCatalog.WorkflowFor(pack, task)), pack.Id)
            .First(s => s.Kind == EngineAssetKind.DiffusionModel);
        var files = new Dictionary<string,string> { [slot.Key] = "retouch.safetensors" };
        service.ModelSelections = _ => files;
        service.EngineIdForTask = _ => pack.Id;
        service.ModelKeyForTask = _ => "server:retouch.safetensors";
        Assert.Same(pack, service.EngineFor(task));
        files[slot.Key] = "another.safetensors";
        Assert.Null(service.EngineFor(task));
        Assert.Same(pack, service.EngineFor(task, pack.Id));
    }

    private const string Flux = "flux2-klein-intel-xpu";
    private const string Gpt = "chatgpt-image-2.5";

    [Fact]
    public void Legacy_preferences_seed_independent_task_and_workflow_profiles_without_loss()
    {
        var settings = Settings.FromJson("""{"AiDefaultsRevision":1,"AiOriginalSize":false,"AiMegapixels":2,"AiReferenceMegapixels":null,"AiMaskGrow":12,"AiMaskBlend":24,"AiMaskBlur":3,"AiMaskContext":2.5,"AiColorMatch":"off","AiSeed":123,"AiVariants":3,"AiVariantMode":1,"AiGptMaskGrow":6,"AiGptMaskBlend":10,"AiGptContextPadding":13,"AiFluxFillMaskGrow":7,"AiFluxFillMaskBlend":9,"AiFluxFillMaskBlur":5,"AiFluxFillMaskContext":1.4,"AiLorasEnabled":true,"AiLoras":[{"Name":"style.safetensors","Strength":0.7}]}""");
        var fill = settings.OperationFor(Flux,AiTaskKind.GenerativeFill);
        Assert.Equal((7,9,5,1.4),(fill.MaskGrow,fill.MaskBlend,fill.MaskBlur,fill.MaskContext));
        var remove = settings.OperationFor(Flux,AiTaskKind.RemoveObject);
        Assert.Equal((12,24,3,2.5),(remove.MaskGrow,remove.MaskBlend,remove.MaskBlur,remove.MaskContext));
        Assert.Equal((false,2.0,(double?)null,123L,3,true),(remove.OriginalSize,remove.Megapixels,remove.ReferenceMegapixels,remove.Seed,remove.Variants,remove.LorasEnabled));
        Assert.Equal("style.safetensors",Assert.Single(remove.Loras).Name);
        var gpt = settings.OperationFor(Gpt,AiTaskKind.GenerativeExpand,true);
        Assert.Equal((6,10,13),(gpt.MaskGrow,gpt.MaskBlend,gpt.GptContextPadding));
        var expand = settings.OperationFor(Flux,AiTaskKind.GenerativeExpand);
        Assert.Equal((true,(double?)1,AiVariantMode.List,16,48,16,2.0,"subtle",-1L),
            (expand.OriginalSize,expand.ReferenceMegapixels,expand.VariantMode,expand.MaskGrow,expand.MaskBlend,expand.MaskBlur,expand.MaskContext,expand.ColorMatch,expand.Seed));
        settings.SetOperation(Flux,AiTaskKind.GenerativeExpand,expand with { MaskBlend=32, ColorMatch="strong", Seed=42, Variants=2, ReferenceMegapixels=0.5, Loras=[] });
        Assert.Equal(JsonSerializer.Serialize(fill),JsonSerializer.Serialize(settings.OperationFor(Flux,AiTaskKind.GenerativeFill)));
        Assert.Equal(JsonSerializer.Serialize(remove),JsonSerializer.Serialize(settings.OperationFor(Flux,AiTaskKind.RemoveObject)));
        Assert.Equal(JsonSerializer.Serialize(gpt),JsonSerializer.Serialize(settings.OperationFor(Gpt,AiTaskKind.GenerativeExpand,true)));
        var loaded = Settings.FromJson(JsonSerializer.Serialize(settings));
        Assert.Equal((32,"strong",42L,2,(double?)0.5),(loaded.OperationFor(Flux,AiTaskKind.GenerativeExpand).MaskBlend,
            loaded.OperationFor(Flux,AiTaskKind.GenerativeExpand).ColorMatch,loaded.OperationFor(Flux,AiTaskKind.GenerativeExpand).Seed,
            loaded.OperationFor(Flux,AiTaskKind.GenerativeExpand).Variants,loaded.OperationFor(Flux,AiTaskKind.GenerativeExpand).ReferenceMegapixels));
        Assert.Equal(JsonSerializer.Serialize(remove),JsonSerializer.Serialize(loaded.OperationFor(Flux,AiTaskKind.RemoveObject)));
    }

    [AvaloniaFact]
    public async Task Expand_advanced_matches_requested_defaults_and_accept_does_not_modify_fill()
    {
        var window = new MainWindow { Width=1280,Height=900 }; window.Settings.CheckForUpdates=false; window.Show();
        try
        {
            window.AiTasks.SelectedEngine=window.AiTasks.Engines.Find(Flux);
            var fillBefore=JsonSerializer.Serialize(window.Settings.OperationFor(Flux,AiTaskKind.GenerativeFill));
            var pending=AiDialogs.Advanced(window,window.Settings,window.AiTasks,AiTaskKind.GenerativeExpand); Dispatcher.UIThread.RunJobs();
            var dialog=Assert.Single(window.OwnedWindows);
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(),text=>text.Text?.StartsWith("Mask ")==true || text.Text=="Color match");
            var memory=Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.Items.OfType<string>().Contains("Lower VRAM"));
            Assert.Equal("Auto",memory.SelectedItem); memory.SelectedItem="Lower VRAM";
            Assert.Contains(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.SelectedItem as string=="Original size");
            Assert.Contains(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.SelectedItem as string=="List · lower VRAM");
            Assert.True(Screenshots.Save(dialog,"flux-expand-independent-advanced"));
            dialog.Close(true); Assert.True(await pending);
            Assert.Equal("subtle",window.Settings.OperationFor(Flux,AiTaskKind.GenerativeExpand).ColorMatch); // retained only for older versions
            Assert.Equal("reduced",Settings.FromJson(JsonSerializer.Serialize(window.Settings)).OperationFor(Flux,AiTaskKind.GenerativeExpand).FluxMemory);
            Assert.Equal(fillBefore,JsonSerializer.Serialize(window.Settings.OperationFor(Flux,AiTaskKind.GenerativeFill)));
            Assert.Equal("off",window.Settings.AiColorMatch);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Switching_workflows_restores_their_own_dialog_drafts_and_cancel_changes_nothing()
    {
        var window = new MainWindow(); window.Settings.CheckForUpdates=false; window.Show();
        try
        {
            var settings = new Settings();
            settings.SetOperation(Flux,AiTaskKind.ImageEdit,settings.OperationFor(Flux,AiTaskKind.ImageEdit) with { OriginalSize=false,Megapixels=0.75,Variants=2 });
            settings.SetOperation(Gpt,AiTaskKind.ImageEdit,settings.OperationFor(Gpt,AiTaskKind.ImageEdit,true) with { OriginalSize=true,Variants=3 });
            const string qwen="qwen-image-2.1-turbo";
            settings.SetOperation(qwen,AiTaskKind.ImageEdit,settings.OperationFor(qwen,AiTaskKind.ImageEdit) with {OriginalSize=false,Megapixels=1.5,Variants=1});
            var before=JsonSerializer.Serialize(settings);
            var pending=AiDialogs.Prompt(window,AiTaskKind.ImageEdit,settings,800,600,service:window.AiTasks); Dispatcher.UIThread.RunJobs();
            var dialog=Assert.Single(window.OwnedWindows);
            var picker=Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.Items.OfType<ImageModelChoice>().Any());
            var size=Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.Items.OfType<string>().Contains("Original size"));
            var variants=Assert.Single(dialog.GetVisualDescendants().OfType<Composa.App.AI.AiVariantButtons>());
            Assert.Equal(AiDimensions.Label(0.75),size.SelectedItem); Assert.Equal(2,variants.Count);
            picker.SelectedItem=picker.Items.OfType<ImageModelChoice>().Single(m => m.Engine.Id == qwen);Dispatcher.UIThread.RunJobs();Assert.Equal(AiDimensions.Label(1.5),size.SelectedItem);Assert.Equal(1,variants.Count);
            picker.SelectedItem=picker.Items.OfType<ImageModelChoice>().Single(m => m.Engine.Id == Gpt); Dispatcher.UIThread.RunJobs(); Assert.Equal("Original size",size.SelectedItem); Assert.Equal(3,variants.Count);
            size.SelectedIndex=3; picker.SelectedItem=picker.Items.OfType<ImageModelChoice>().Single(m => m.Engine.Id == Flux); Dispatcher.UIThread.RunJobs(); Assert.Equal(AiDimensions.Label(0.75),size.SelectedItem); Assert.Equal(2,variants.Count);
            dialog.Close(false); Assert.Null(await pending); Assert.Equal(before,JsonSerializer.Serialize(settings));
        }
        finally { window.Close(); }
    }
}
