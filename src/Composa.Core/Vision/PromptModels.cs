namespace Composa.Vision;

public enum PromptModelKind { MobileSam, EfficientSamTi }
public sealed record PromptModelFile(string Id, string Name, string File, string Sha256, long Bytes, string Source, string Url)
    : OnnxModel(Id, Name, File, Sha256, Bytes, "Apache-2.0", Name + " authors", Source, Url);
public sealed record PromptModel(PromptModelKind Kind, string Name, PromptModelFile Encoder, PromptModelFile Decoder);

public static class PromptModels
{
    public static PromptModel MobileSam { get; } = new(PromptModelKind.MobileSam, "MobileSAM",
        new("mobile-sam-encoder", "MobileSAM encoder", "mobile_sam_encoder.onnx", "20deef402855b31222b528f52b04807e41ebe47216ac0e39a0729f43491a0209", 28157093, "https://github.com/ChaoningZhang/MobileSAM", "https://huggingface.co/vietanhdev/segment-anything-onnx-models/resolve/main/mobile_sam_20230629.zip"),
        new("mobile-sam-decoder", "MobileSAM decoder", "mobile_sam_decoder.onnx", "22cf85e35d14182f4b4712364264c06b22edbef63f065189586f080ef4e2f325", 16500272, "https://github.com/facebookresearch/segment-anything", "https://huggingface.co/vietanhdev/segment-anything-onnx-models/resolve/main/mobile_sam_20230629.zip"));
    public static PromptModel EfficientSamTi { get; } = new(PromptModelKind.EfficientSamTi, "EfficientSAM Ti",
        new("efficient-sam-ti-encoder", "EfficientSAM Ti encoder", "efficient_sam_vitt_encoder.onnx", "7a73ee65aa2c37237c89b4b18e73082f757ffb173899609c5d97a2bbd4ebb02d", 24799761, "https://github.com/yformer/EfficientSAM", "https://github.com/wkentaro/efficient-sam/releases/download/onnx-models-20231225/efficient_sam_vitt_encoder.onnx"),
        new("efficient-sam-ti-decoder", "EfficientSAM Ti decoder", "efficient_sam_vitt_decoder.onnx", "e1afe46232c3bfa3470a6a81c7d3181836a94ea89528aff4e0f2d2c611989efd", 16565728, "https://github.com/yformer/EfficientSAM", "https://github.com/wkentaro/efficient-sam/releases/download/onnx-models-20231225/efficient_sam_vitt_decoder.onnx"));
}
