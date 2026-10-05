// Ported from Lolly (github.com/lolly-tools/lolly, packages/node-shell/src/ml/matte-models.ts and scripts/fetch-matte-models.ts at 12b26ff), MPL-2.0, used under the MIT licence by permission of Andy Fitzsimon, 2026-09-30.
namespace Composa.Vision;

/// <summary>How a model's single-channel output becomes a 0 to 1 matte.</summary>
public enum MaskActivation
{
    /// <summary>The head is already bounded; stretch what it gave to the full range, as rembg does.</summary>
    MinMax,
    /// <summary>The head is a logit; squash it.</summary>
    Sigmoid
}

/// <summary>
/// One on-device segmentation model: where its file is, what the file must hash to, and the tensor contract the
/// runner needs. The normalization and the activation differ per model and a wrong value does not crash, it quietly
/// ruins the matte, so every value here was confirmed against the real graph (see <c>SubjectModelTests</c>).
/// </summary>
public sealed record SubjectModel(
    string Id,
    string Name,
    string File,
    string Sha256,
    long Bytes,
    string Licence,
    string Attribution,
    string Source,
    string Url,
    int InputSize,
    float[] Mean,
    float[] Std,
    MaskActivation Activation,
    string Note) : OnnxModel(Id, Name, File, Sha256, Bytes, Licence, Attribution, Source, Url)
{
    public bool StretchInput { get; init; }
}

/// <summary>
/// The models Composa knows. Only weights under a permissive licence (Apache-2.0, MIT or BSD) are listed: BRIA's RMBG
/// models are non-commercial and never ship. Every entry's licence, source and hash is also recorded in
/// <c>packaging/THIRD-PARTY-NOTICES.txt</c>, which travels with the files.
/// </summary>
public static class SubjectModels
{
    private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    /// <summary>U²-Net lite: a saliency net for any subject, 4.6 MB at 320², and the default. Edges are soft, and busy or low-contrast backgrounds can confuse it.</summary>
    public static readonly SubjectModel U2NetP = new(
        Id: "u2netp",
        Name: "Any subject",
        File: "u2netp.onnx",
        Sha256: "309c8469258dda742793dce0ebea8e6dd393174f89934733ecc8b14c76f4ddd8",
        Bytes: 4_574_861,
        Licence: "Apache-2.0",
        Attribution: "U²-Net, Copyright (c) 2020 Xuebin Qin et al.",
        Source: "https://github.com/xuebinqin/U-2-Net",
        Url: "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx",
        InputSize: 320,
        Mean: ImageNetMean,
        Std: ImageNetStd,
        Activation: MaskActivation.MinMax,
        Note: "Works on any subject. Edges are soft, and busy or low-contrast backgrounds can confuse it.");

    /// <summary>MODNet: a portrait matting net, 26 MB, run at 512². Follows hair and soft edges; weaker on anything that is not a person.</summary>
    public static readonly SubjectModel ModNet = new(
        Id: "modnet",
        Name: "Person",
        File: "modnet.onnx",
        Sha256: "07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9",
        Bytes: 25_888_640,
        Licence: "Apache-2.0",
        Attribution: "MODNet, Copyright (c) 2020 Zhanghan Ke et al.",
        Source: "https://github.com/ZHKKKe/MODNet",
        Url: "https://huggingface.co/Xenova/modnet/resolve/main/onnx/model.onnx",
        InputSize: 512,
        Mean: [0.5f, 0.5f, 0.5f],
        Std: [0.5f, 0.5f, 0.5f],
        Activation: MaskActivation.MinMax,
        Note: "Tuned for people: soft hair and edges. Weaker on anything that is not a person.");

    public static readonly IReadOnlyList<SubjectModel> All = [U2NetP, ModNet];

    /// <summary>Optional full BiRefNet weights, kept outside the installer. Pin the graph and tensor contract.</summary>
    public static readonly SubjectModel BiRefNet = new(
        "birefnet", "BiRefNet · High quality", "birefnet.onnx",
        "58f621f00f5d756097615970a88a791584600dcf7c45b18a0a6267535a1ebd3c", 972_666_916, "MIT",
        "BiRefNet, Copyright (c) 2024 ZhengPeng; ONNX conversion by onnx-community",
        "https://github.com/ZhengPeng7/BiRefNet",
        "https://huggingface.co/onnx-community/BiRefNet-ONNX/resolve/9b057984031590f02a042f18187c76c8d818a26b/onnx/model.onnx",
        1024, ImageNetMean, ImageNetStd, MaskActivation.Sigmoid,
        "Optional 973 MB model; CPU inference can use about 9 GB RAM. Weights are released after each operation.") { StretchInput = true };

    public static string? BiRefNetPath { get; set; }
    public static SubjectModel NativeBiRefNet => string.IsNullOrWhiteSpace(BiRefNetPath) ? BiRefNet : BiRefNet with { File = System.IO.Path.GetFullPath(BiRefNetPath) };

    /// <summary>Where the model files are; see <see cref="OnnxModels.Directory"/>.</summary>
    public static string Directory => OnnxModels.Directory;

    public static SubjectModel? Find(string id) => id == BiRefNet.Id ? NativeBiRefNet : All.FirstOrDefault(m => m.Id == id);
}
