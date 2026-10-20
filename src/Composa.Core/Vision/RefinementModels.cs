namespace Composa.Vision;

public sealed record RefinementModel(string Id, string Name, string File, string Sha256, long Bytes, string Licence, string Attribution, string Source, string Url)
    : OnnxModel(Id, Name, File, Sha256, Bytes, Licence, Attribution, Source, Url);

public static class RefinementModels
{
    public static readonly RefinementModel VitMatte = new(
        "vitmatte-small", "AI Matting", "vitmatte_small.onnx",
        "bf28d2e0be2c073286e88d60ad649d7123da2749a2d99133fd1098d5887e0225", 103_885_865,
        "Apache-2.0", "ViTMatte, Hust Vision Lab; ONNX conversion by Xenova / Hugging Face",
        "https://huggingface.co/hustvl/vitmatte-small-composition-1k",
        "https://huggingface.co/Xenova/vitmatte-small-composition-1k/resolve/6bc1297f6140f055a227b6d2cfe8c093281f35d2/onnx/model.onnx");
}
