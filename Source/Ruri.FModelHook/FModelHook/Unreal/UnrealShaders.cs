using AssetRipper.Import.Logging;
using AssetRipper.SourceGenerated;
using Ruri.FModelHook.ShaderDecompiler;
using Ruri.RipperHook.CabMapping;
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
/// Nothing is scanned. Only the packages asked for are loaded, only the archives their materials
/// actually live in are opened, only their tables are read, and only their own shaders are pulled
/// out of them -- so the cost is what was asked for, and the only thing that lands on disk is the
/// source. Asking for the whole install is the SAME question with every material named, not a
/// different mode: the map already says which packages those are, so even that names its subjects
/// instead of sweeping.
/// </summary>
public static class UnrealShaders
{
    public const string ShadersId = "unreal.shaders";
    public const string AllShadersId = "unreal.shaders.all";
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

        Datasets.Publish(AllShadersId, DataRole.Payload,
            [DataParam.Text(OutputParam)],
            "Decompile every shader this install ships, into the stated folder -- the same answer "
            + "as the packaged question with nothing named to narrow it. Which packages those are "
            + "is read off the mounted map, which already states what every package holds.",
            AllShaders);
    }

    private static ColumnTable Shaders(DataRequest request)
    {
        string[] packages = request.List(PackagesParam);
        if (packages.Length == 0)
        {
            throw new ArgumentException($"dataset '{ShadersId}' answers about packages; name them with '{PackagesParam}'.");
        }
        return Decompile(ShadersId, request, packages);
    }

    /// <summary>
    /// Every material package the mounted map lists, as the subjects of one run.
    ///
    /// Unreal ships no shader asset, so "all the shaders" IS "every material": the program is the
    /// material's own compiled map. The map already states what each package holds -- it was read
    /// once when the map was built -- so this names them from there rather than re-opening every
    /// package in the install to find out.
    /// </summary>
    private static ColumnTable AllShaders(DataRequest request)
    {
        CabTable map = request.Map;
        List<string> packages = [];
        for (int id = 0; id < map.Count; id++)
        {
            if (map.ClassIds(id).Contains((int)ClassIDType.Material))
            {
                packages.Add(map.CabName(id));
            }
        }
        Say($"[ShaderSource] the map lists {packages.Count} material package(s) in this install.");
        return Decompile(AllShadersId, request, [.. packages]);
    }

    private static ColumnTable Decompile(string id, DataRequest request, string[] packages)
    {
        TableBuilder table = new(id, "archive", "shaderMaps#", "output");
        string output = request.Text(OutputParam);
        if (output.Length == 0)
        {
            throw new ArgumentException($"dataset '{id}' writes files; state where with '{OutputParam}'.");
        }

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        long mountMs = clock.ElapsedMilliseconds;
        ShaderSourceSummary summary = ShaderSourceRun.Execute(new ShaderSourceRequest
        {
            Provider = provider,
            Subjects = Array.ConvertAll(packages, static path => (IShaderMapSubject)new PackageSubject(path)),
            OutputDirectory = output,
            SplitVariantsToHlslFiles = true,
            Log = Say,
            LogError = Complain,
        });
        Say($"[ShaderSource] dataset answered in {clock.ElapsedMilliseconds} ms (provider ready after {mountMs} ms).");

        foreach (ShaderSourceArchive archive in summary.Archives)
        {
            table.Row(archive.Archive, archive.ShaderMaps, archive.OutputDirectory);
        }
        return table.Build();
    }

    private static void Say(string message) => Logger.Info(LogCategory.Import, message);

    private static void Complain(string message) => Logger.Warning(LogCategory.Import, message);
}
