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
    public async Task Fill_dialog_shows_total_request_resolution_in_both_size_modes()
    {
        var window=new MainWindow(); window.Settings.CheckForUpdates=false; window.Show();
        try
        {
            window.Settings.SetOperation(Flux,AiTaskKind.GenerativeFill,window.Settings.OperationFor(Flux,AiTaskKind.GenerativeFill)
                with { OriginalSize=false,Megapixels=0.5,MaskGrow=4,MaskBlend=8,MaskBlur=4,MaskContext=1.2 });
            var before=JsonSerializer.Serialize(window.Settings);
            var pending=AiDialogs.Prompt(window,AiTaskKind.GenerativeFill,window.Settings,264,176,service:window.AiTasks,
                hasSelection:true,sourceCanvas:new(0,0,1537,991),selectionBounds:new(600,350,864,526));
            Dispatcher.UIThread.RunJobs(); var dialog=Assert.Single(window.OwnedWindows);
            var label=Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(),text=>text.Text?.StartsWith("FLUX request including context:")==true);
            Assert.Contains("MP",label.Text); Assert.Contains("848 × 608 px",label.Text);
            var size=Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),combo=>combo.Items.Cast<string>().Contains("Original size"));
            size.SelectedItem="Original size"; Dispatcher.UIThread.RunJobs();
            Assert.Equal($"FLUX request including context: 384 × 272 px · {0.1:0.##} MP",label.Text);
            size.SelectedItem=AiDimensions.Label(0.5); Dispatcher.UIThread.RunJobs();
            Assert.True(Screenshots.Save(dialog,"flux-selection-resolution-and-context"));
            dialog.Close(false); Assert.Null(await pending); Assert.Equal(before,JsonSerializer.Serialize(window.Settings));
        }
        finally { window.Close(); }
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
            Assert.Equal(new[] { 16.0,48,16,2 },dialog.GetVisualDescendants().OfType<Composa.App.Controls.SliderField>().Take(4).Select(f=>Math.Round(f.Value,6)));
            var color=Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.Items.Cast<string>().Contains("subtle"));
            var memory=Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.Items.Cast<string>().Contains("Lower VRAM"));
            Assert.Equal("Auto",memory.SelectedItem); memory.SelectedItem="Lower VRAM";
            Assert.Equal("subtle",color.SelectedItem);
            Assert.Contains(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.SelectedItem as string=="Original size");
            Assert.Contains(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.SelectedItem as string=="List · lower VRAM");
            Assert.True(Screenshots.Save(dialog,"flux-expand-independent-advanced"));
            color.SelectedItem="strong"; dialog.Close(true); Assert.True(await pending);
            Assert.Equal("strong",window.Settings.OperationFor(Flux,AiTaskKind.GenerativeExpand).ColorMatch);
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
            var before=JsonSerializer.Serialize(settings);
            var pending=AiDialogs.Prompt(window,AiTaskKind.ImageEdit,settings,800,600,service:window.AiTasks); Dispatcher.UIThread.RunJobs();
            var dialog=Assert.Single(window.OwnedWindows);
            var picker=Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.Items.Cast<string>().Contains("CHAT GPT 2.5"));
            var size=Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.Items.Cast<string>().Contains("Original size"));
            var variants=Assert.Single(dialog.GetVisualDescendants().OfType<ComboBox>(),c=>c.Width==60);
            Assert.Equal(AiDimensions.Label(0.75),size.SelectedItem); Assert.Equal("2",variants.SelectedItem);
            picker.SelectedIndex=1; Dispatcher.UIThread.RunJobs(); Assert.Equal("Original size",size.SelectedItem); Assert.Equal("3",variants.SelectedItem);
            size.SelectedIndex=3; picker.SelectedIndex=0; Dispatcher.UIThread.RunJobs(); Assert.Equal(AiDimensions.Label(0.75),size.SelectedItem); Assert.Equal("2",variants.SelectedItem);
            dialog.Close(false); Assert.Null(await pending); Assert.Equal(before,JsonSerializer.Serialize(settings));
        }
        finally { window.Close(); }
    }
}
