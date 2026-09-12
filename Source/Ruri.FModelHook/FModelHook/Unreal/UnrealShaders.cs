using AssetRipper.Import.Logging;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using Ruri.FModelHook.ShaderDecompiler;
using Ruri.FModelHook.ShaderDecompiler.Headless;
using Ruri.FModelHook.Unreal.Readers;
using Ruri.RipperHook.Data;
using Ruri.RipperHook.Tables;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// What one package's materials are actually COMPILED as, decompiled to source beside each other.
///
/// Unreal ships no shader objects. A material's program lives as blobs in a shader archive shared
/// by thousands of other materials, keyed by the hash of the material's compiled shader map, and
/// the only way back to readable code is: resolve the material, find the map it compiled to, find
/// which archive carries that hash, and decompile the entries of that hash out of it. Every one of
/// those steps is already in this assembly -- this is the one that states them as a host action.
///
/// Nothing is scanned. Only the package asked for is loaded, only the archives its materials
/// actually live in are opened, and only its own shader maps are decompiled, so the cost is the
/// character's, not the install's.
/// </summary>
public static class UnrealShaders
{
    public const string ShadersId = "unreal.shaders";
    public const string PackageParam = "package";
    public const string OutputParam = "output";

    public static void Register()
    {
        Datasets.Publish(ShadersId, DataRole.Payload,
            [DataParam.Text(PackageParam), DataParam.Text(OutputParam)],
            "Decompile every shader variant the materials of one package compiled to, into the "
            + "stated folder: one row per archive that carried them, with how many of the package's "
            + "materials it answered for. Unreal has no shader asset to export -- a material's "
            + "program is blobs in a shared archive -- so what lands is the vertex and pixel stages "
            + "as source, one file per variant, beside the metadata naming which material each came "
            + "from.",
            Shaders);
    }

    private static ColumnTable Shaders(DataRequest request)
    {
        TableBuilder table = new(ShadersId, "archive", "materials#", "output");
        UnrealFileProvider provider = UnrealProviderSession.Open(request.GameRoot);
        string package = request.Text(PackageParam);
        string output = request.Text(OutputParam);
        if (output.Length == 0)
        {
            throw new ArgumentException($"dataset '{ShadersId}' writes files; state where with '{OutputParam}'.");
        }
        string[] materials = Materials(provider, package);
        if (materials.Length == 0)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] '{package}' names no material, so it compiled no shader.");
            return table.Build();
        }
        List<HeadlessShaderExportRunner.MaterialShaderLocation> located =
            HeadlessShaderExportRunner.FindShaderArchivesForMaterials(provider, materials, Say, Complain);

        string filter = Narrowest(located);
        ExportPipelineState state = new()
        {
            Provider = provider,
            ProjectOutputRoot = output,
            Log = Say,
            LogError = Complain,
        };
        state.MaterialScope = Scope(materials, located);
        foreach (string archive in located.SelectMany(static one => one.ArchivePaths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!provider.Files.TryGetValue(archive, out GameFile? file))
            {
                continue;
            }
            string basePath = Path.Combine(output, Path.GetFileNameWithoutExtension(archive)).Replace('\\', '/');
            ShaderArchiveExporter.ProcessArchive(state, file, basePath, splitVariants: true, skipDecompile: false, materialFilter: filter);
            table.Row(archive, located.Count(one => one.ArchivePaths.Contains(archive, StringComparer.OrdinalIgnoreCase)),
                Path.Combine(Path.GetDirectoryName(basePath)!, "Decompiled", Path.GetFileName(basePath)).Replace('\\', '/'));
        }
        return table.Build();
    }

    /// <summary>Every material the package's own meshes name, the same set an import would build.</summary>
    private static string[] Materials(UnrealFileProvider provider, string package)
    {
        string key = UnrealDataTables.Key(provider, package);
        if (!provider.Files.TryGetValue(key, out GameFile? file))
        {
            throw new FileNotFoundException($"[Unreal] the mount holds no package '{package}'.", package);
        }
        HashSet<string> named = new(StringComparer.OrdinalIgnoreCase);
        foreach (UObject export in provider.LoadPackage(file).GetExports())
        {
            foreach (string path in UnrealComponents.MaterialPaths(export, []))
            {
                // As the MOUNT lists it. A mesh names its materials the way the engine writes a
                // reference -- content root, object appended -- and the shared reader asks the
                // mount for exactly the key it holds.
                if (path.Length > 0)
                {
                    named.Add(UnrealDataTables.Key(provider, path));
                }
            }
        }
        return named.ToArray();
    }

    /// <summary>
    /// The material packages this run is about: the ones the mesh names, plus the templates they
    /// inherit their compiled shader map from. Stated so the bridge is built over these instead of
    /// every material the install ships -- measured at tens of gigabytes and an hour on a large
    /// title, for one character.
    /// </summary>
    private static IReadOnlyCollection<string> Scope(string[] materials, List<HeadlessShaderExportRunner.MaterialShaderLocation> located)
    {
        HashSet<string> scope = new(StringComparer.OrdinalIgnoreCase);
        foreach (string material in materials)
        {
            scope.Add(StripExtension(material));
        }
        foreach (HeadlessShaderExportRunner.MaterialShaderLocation one in located)
        {
            scope.Add(StripExtension(one.MaterialPath));
            scope.Add(StripExtension(one.OwningMaterialPath));
        }
        scope.Remove(string.Empty);
        return scope;
    }

    private static string StripExtension(string path)
    {
        int slash = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        return dot > slash ? path[..dot] : path;
    }

    /// <summary>
    /// What keeps the decompile to this character's shaders and off the quarter-million others the
    /// same archive carries. The archive names its entries by MATERIAL, so the mesh's own name
    /// matches none of them; the folder the character's materials actually sit in matches all of
    /// them and little else. It is read off the materials themselves rather than assumed, and a
    /// cast whose materials are scattered falls back to the one thing they all share. It is the
    /// OWNING template that is matched, because that is what a compiled shader map belongs to and
    /// what the metadata keys entries by -- a material instance owns no map of its own.
    /// </summary>
    private static string Narrowest(List<HeadlessShaderExportRunner.MaterialShaderLocation> located)
    {
        string[] owners = located.Select(static one => one.OwningMaterialPath)
            .Where(static path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (owners.Length == 0)
        {
            return string.Empty;
        }
        string shared = UnrealPaths.Folder(owners[0]);
        foreach (string owner in owners)
        {
            string folder = UnrealPaths.Folder(owner);
            while (shared.Length > 0 && !folder.StartsWith(shared, StringComparison.OrdinalIgnoreCase))
            {
                shared = UnrealPaths.Folder(shared);
            }
        }
        return shared.Length > 0 ? shared : UnrealPaths.Stem(owners[0]);
    }

    private static void Say(string message) => Logger.Info(LogCategory.Import, message);

    private static void Complain(string message) => Logger.Warning(LogCategory.Import, message);
}
