using AssetRipper.Assets;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions;
using Ruri.RipperHook.CabMapping;
using Ruri.RipperHook.BlenderBridge.Statements;
using Ruri.RipperHook.BlenderBridge.Tables;

namespace Ruri.RipperHook.BlenderBridge.Data;

/// <summary>
/// Shaders written out as source: what a selection SHADES WITH (the materials are walked,
/// each naming its shader and the keywords it enables, which is the variant compiled for
/// that row), or every shader the install ships. Not part of an import: no host here can use
/// a compiled shader, so reading them is a question asked on its own.
/// </summary>
public static class ShaderDatasets
{
    public const string ShadersId = "core.shaders";
    public const string AllShadersId = "core.shaders.all";
    public const string VariantsId = "core.shaders.variants";
    public const string Seed = "seed";
    public const string Output = "output";

    public static void Register()
    {
        Datasets.Publish(ShadersId, DataRole.Diagnostic, [DataParam.List(Seed), DataParam.Text(Output)],
            "Every shader the seeds' materials shade with, written as source under output: one row "
            + "per (material, shader, variant), with where it landed and how big it came out. A seed "
            + "is read as a load reads it, so these are the shaders loading it would shade with.", Shaders);
        Datasets.Publish(AllShadersId, DataRole.Diagnostic, [DataParam.Text(Output)],
            "Every shader the install ships, written as source under output. A shader no material "
            + "references is still a shader the install ships; reached this way it states no variant.", AllShaders);
        Datasets.Publish(VariantsId, DataRole.Diagnostic, [DataParam.List(Seed)],
            "The variants a selection's materials ask for: one row per distinct (shader, enabled keywords), with how "
            + "many materials ask for it and one of them by name. Nothing is written: the shader sources are the "
            + "other datasets' question.", Variants);
    }

    private static ColumnTable Variants(DataRequest request)
    {
        Dictionary<(string Shader, string Keywords), (int Count, string Example)> asked = new();
        GameData? loaded = ClosureReader.Read(request.Map, StatementSources.Archives(request.List(Seed), request.Map),
            reachThroughDependents: true);
        if (loaded is not null)
        {
            foreach (IUnityObjectBase asset in loaded.GameBundle.FetchAssets())
            {
                request.Cancellation.ThrowIfCancellationRequested();
                if (asset is not IMaterial material || material.Shader_C21.TryGetAsset(material.Collection) is not IShader shader)
                {
                    continue;
                }
                string stated = UnityMaterials.ShaderNameOf(shader);
                (string, string) key = (stated.Length > 0 ? stated : shader.GetBestName(), EnabledKeywords(material));
                asked[key] = asked.TryGetValue(key, out (int Count, string Example) known) ? (known.Count + 1, known.Example) : (1, material.GetBestName());
            }
        }
        TableBuilder table = new(VariantsId, "name|Shader", "keywords|Variant", "materials#|Materials", "example|Example");
        table.Role(ColumnRole.Label, "name").Role(ColumnRole.Detail, "keywords");
        foreach (((string shader, string keywords), (int count, string example)) in asked
                     .OrderBy(pair => pair.Key.Shader, StringComparer.Ordinal).ThenByDescending(pair => pair.Value.Count))
        {
            table.Row(shader, keywords, (double)count, example);
        }
        return table.Build();
    }

    /// <summary>A material's enabled keywords come from the one reader that knows all three
    /// serialisations Unity has used for them; reading one of the three here would report an
    /// empty variant on every build that spells them another way, and throw on the builds
    /// where that field is absent entirely.</summary>
    private static string EnabledKeywords(IMaterial material)
    {
        List<string> enabled = [.. UnityMaterials.Read(material, static _ => string.Empty).KeywordList];
        enabled.Sort(StringComparer.Ordinal);
        return string.Join(' ', enabled);
    }

    private static ColumnTable Shaders(DataRequest request)
    {
        string output = request.Text(Output);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ShaderWriter writer = new(output, ShadersId);
        GameData? loaded = ClosureReader.Read(request.Map, StatementSources.Archives(request.List(Seed), request.Map),
            reachThroughDependents: true);
        if (loaded is null)
        {
            return writer.Build();
        }
        foreach (IUnityObjectBase asset in loaded.GameBundle.FetchAssets())
        {
            request.Cancellation.ThrowIfCancellationRequested();
            if (asset is IMaterial material && material.Shader_C21.TryGetAsset(material.Collection) is IShader shader)
            {
                writer.Write(shader, material);
            }
        }
        return writer.Build();
    }

    private static ColumnTable AllShaders(DataRequest request)
    {
        string output = request.Text(Output);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        ShaderWriter writer = new(output, AllShadersId);
        CabTable map = request.Map;
        List<string> cabs = [];
        for (int id = 0; id < map.Count; id++)
        {
            if (map.ClassIds(id).Contains((int)ClassIDType.Shader))
            {
                cabs.Add(map.CabName(id));
            }
        }
        GameData? loaded = ClosureReader.Read(map, cabs);
        if (loaded is null)
        {
            return writer.Build();
        }
        foreach (IUnityObjectBase asset in loaded.GameBundle.FetchAssets())
        {
            request.Cancellation.ThrowIfCancellationRequested();
            if (asset is IShader shader)
            {
                writer.Write(shader, null);
            }
        }
        return writer.Build();
    }

    /// <summary>One shader per file, one row per reason it was asked for; a shader reached
    /// twice is written once and named once.</summary>
    private sealed class ShaderWriter
    {
        private readonly Dictionary<IShader, string> _written = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
        private readonly TableBuilder _table;
        private readonly string _outputDir;

        public ShaderWriter(string outputDir, string id)
        {
            _outputDir = outputDir;
            _table = new TableBuilder(id, "material|Material", "name|Shader", "keywords|Variant", "file|File", "bytes#|Size", "cab|Cab");
            _table.Role(ColumnRole.Label, "name").Role(ColumnRole.Detail, "keywords").Role(ColumnRole.Group, "material");
        }

        /// <summary>A shader's own name is the one ShaderLab states, which is what a person
        /// recognises it by; the asset's m_Name is empty in a stripped build.</summary>
        public void Write(IShader shader, IMaterial? material)
        {
            string stated = UnityMaterials.ShaderNameOf(shader);
            string name = stated.Length > 0 ? stated : shader.GetBestName();
            if (!_written.TryGetValue(shader, out string? file))
            {
                Directory.CreateDirectory(_outputDir);
                file = Path.Combine(_outputDir, Readable(name) + ".shader");
                for (int copy = 2; !_taken.Add(file); copy++)
                {
                    file = Path.Combine(_outputDir, Readable(name) + "_" + copy + ".shader");
                }
                if (!AR.ShaderContentExtractor.Instance.Export(shader, file, LocalFileSystem.Instance))
                {
                    return;
                }
                _written[shader] = file;
            }
            _table.Row(material is null ? string.Empty : material.GetBestName(), name,
                material is null ? string.Empty : EnabledKeywords(material), file, new FileInfo(file).Length, shader.Collection.Name);
        }

        public ColumnTable Build() => _table.Build();

        private static string Readable(string name)
        {
            char[] made = name.ToCharArray();
            char[] bad = Path.GetInvalidFileNameChars();
            for (int index = 0; index < made.Length; index++)
            {
                if (Array.IndexOf(bad, made[index]) >= 0)
                {
                    made[index] = '_';
                }
            }
            string readable = new string(made).Trim();
            return readable.Length == 0 ? "shader" : readable;
        }
    }
}
