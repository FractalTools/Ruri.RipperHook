using AssetRipper.Import.Logging;
using Ruri.FModelHook.ShaderDecompiler;
using Ruri.RipperHook.Data;
using Ruri.RipperHook.Tables;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// What one package's materials are actually COMPILED as, decompiled to source beside each other.
///
/// Unreal ships no shader objects. A material's program lives as blobs in a shader archive shared
/// by thousands of other materials, keyed by the hash of the material's compiled shader map, and
/// the only way back to readable code is: resolve the material, find the map it compiled to, find
/// which archive carries that hash, and decompile the entries of that hash out of it.
///
/// Nothing is scanned. Only the package asked for is loaded, only the archives its materials
/// actually live in are opened, only their tables are read, and only its own shaders are pulled
/// out of them -- so the cost is the character's, not the install's, and the only thing that lands
/// on disk is the source.
/// </summary>
public static class UnrealShaders
{
    public const string ShadersId = "unreal.shaders";
    public const string PackagesParam = "packages";
    public const string OutputParam = "output";

    public static void Register()
    {
        Datasets.Publish(ShadersId, DataRole.Payload,
            [DataParam.List(PackagesParam), DataParam.Text(OutputParam)],
            "Decompile every shader variant the stated packages compiled to, into the stated "
            + "folder: one row per archive that carried them, with how many shader maps it answered "
            + "for. A package answers as whatever it is -- a material for itself, a mesh or an actor "
            + "for every material it names, an effect for its own scripts. Unreal has no shader "
            + "asset to export -- a material's program is blobs in a shared archive -- so what lands "
            + "is the vertex and pixel stages as source, one file per variant, beside the metadata "
            + "naming which material each came from.",
            Shaders);
    }

    private static ColumnTable Shaders(DataRequest request)
    {
        TableBuilder table = new(ShadersId, "archive", "shaderMaps#", "output");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        string[] packages = request.List(PackagesParam);
        string output = request.Text(OutputParam);
        if (output.Length == 0)
        {
            throw new ArgumentException($"dataset '{ShadersId}' writes files; state where with '{OutputParam}'.");
        }
        if (packages.Length == 0)
        {
            throw new ArgumentException($"dataset '{ShadersId}' answers about packages; name them with '{PackagesParam}'.");
        }

        ShaderSourceSummary summary = ShaderSourceRun.Execute(new ShaderSourceRequest
        {
            Provider = provider,
            Subjects = Array.ConvertAll(packages, static path => (IShaderMapSubject)new PackageSubject(path)),
            OutputDirectory = output,
            SplitVariantsToHlslFiles = true,
            Log = Say,
            LogError = Complain,
        });

        foreach (ShaderSourceArchive archive in summary.Archives)
        {
            table.Row(archive.Archive, archive.ShaderMaps, archive.OutputDirectory);
        }
        return table.Build();
    }

    private static void Say(string message) => Logger.Info(LogCategory.Import, message);

    private static void Complain(string message) => Logger.Warning(LogCategory.Import, message);
}
