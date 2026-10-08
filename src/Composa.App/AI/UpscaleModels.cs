using Composa.AI;

namespace Composa.App.AI;

public sealed record UpscaleModelChoice(EngineProfile Engine, WorkflowModelSlot Slot, string Model)
{
    public override string ToString() => Model + (Engine.Id.StartsWith("seedvr2", StringComparison.Ordinal) ? " · " + Engine.DisplayName : "");
}

public static class UpscaleModels
{
    public static IReadOnlyList<WorkflowModelSlot> Slots(EngineCatalog catalog, EngineProfile engine) => engine.Binding(AiTaskKind.Upscale) is { } binding
        ? WorkflowModels.Slots(catalog.ReadWorkflow(engine, engine.Workflow(binding.Workflow)), engine.Id) : [];

    public static IEnumerable<string> Available(WorkflowModelSlot slot, IEnumerable<string> available)
    {
        if (!slot.EngineId.StartsWith("seedvr2", StringComparison.Ordinal)) return available;
        // The native UNET/VAE loaders also list FLUX files. Those are incompatible
        // with SeedVR2 even though they happen to use the same loader input.
        return available.Where(name => slot.Kind == EngineAssetKind.DiffusionModel
            ? Path.GetFileName(name.Replace('\\', '/')).Contains("seedvr2", StringComparison.OrdinalIgnoreCase)
            : slot.Kind != EngineAssetKind.Vae || name.Contains("ema_vae", StringComparison.OrdinalIgnoreCase) || name.Contains("seedvr", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<UpscaleModelChoice> Choices(AiTaskService service)
    {
        if (service.ServerCapabilities is not { } capabilities || service.ConnectionState != ComfyConnectionState.Connected) return [];
        return service.Engines.Profiles.Where(engine => engine.Binding(AiTaskKind.Upscale) is { } binding
            && service.Engines.ReadWorkflow(engine, engine.Workflow(binding.Workflow)).Select(pair => pair.Value?["class_type"]?.GetValue<string>())
                .Where(type => type != null).All(type => capabilities.NodeTypes.Contains(type!)))
            .SelectMany(engine => Slots(service.Engines, engine).Where(slot => slot.Kind == EngineAssetKind.Upscaler
                || engine.Id.StartsWith("seedvr2", StringComparison.Ordinal) && slot.Kind == EngineAssetKind.DiffusionModel)
                .SelectMany(slot => Available(slot, capabilities.ModelChoices.GetValueOrDefault(slot.LoaderKey) ?? [])
                    .Select(model => new UpscaleModelChoice(engine, slot, model))))
            .DistinctBy(choice => (choice.Engine.Id.StartsWith("seedvr2", StringComparison.Ordinal) ? choice.Engine.Id : "upscaler", choice.Model))
            .OrderBy(choice => choice.Model, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
