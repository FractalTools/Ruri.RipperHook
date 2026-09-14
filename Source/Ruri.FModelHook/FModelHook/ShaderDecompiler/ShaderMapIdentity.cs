using CUE4Parse.UE4.Assets.Exports.Material;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// What each shader of one map IS: the shader type it was compiled from, the vertex factory it
/// was compiled for, which pipeline it belongs to and which permutation of its type it is.
///
/// Every bit of that is stated by the material's OWN compiled map -- the archive stores code and
/// hashes and nothing else -- so this reads it there and nowhere else. The archive contributes
/// only the join: the map's shaders in the order it lists them, which is the order the material
/// states its resource indices in.
///
/// Names come from what the map itself carries: its pointer table names the layouts it depends
/// on, and a name hashes back to the hash the shader was written with. Hashes the map cannot
/// name are looked up in the indexes dumped from the engine's own type registrations.
/// </summary>
internal static class ShaderMapIdentity
{
    private const int ContainerKeyHashLength = 12;
    private const string EmptyVertexFactoryHash = "0000000000000000";

    private sealed record Record(
        int ResourceIndex,
        string ShaderTypeHash,
        string ShaderTypeName,
        string VertexFactoryTypeHash,
        string VertexFactoryTypeName,
        int PermutationId,
        string PipelineTypeHash,
        string PipelineTypeName);

    /// <summary>
    /// The identity of each shader the map owns, keyed by the shader's index in the archive.
    /// </summary>
    public static Dictionary<int, ShaderContainerInfo> Of(
        ShaderMapTarget target,
        ShaderMapCatalog.Placement placement,
        string materialName,
        ShaderTypeSeedRegistry shaderTypes,
        HashNameIndex vertexFactoryTypes,
        HashNameIndex pipelineTypes)
    {
        List<Record> ordered = target.ShaderMap?.Content is FMaterialShaderMapContent content
            ? Ordered(content, Names(target.ShaderMap))
            : new List<Record>();
        Dictionary<int, List<Record>> byResourceIndex = new();
        foreach (Record record in ordered)
        {
            if (!byResourceIndex.TryGetValue(record.ResourceIndex, out List<Record>? bucket))
            {
                bucket = new List<Record>();
                byResourceIndex[record.ResourceIndex] = bucket;
            }
            bucket.Add(record);
        }

        ShaderLibrary library = placement.Library;
        string containerKey = "SM" + Shorten(target.ShaderMapHash);
        Dictionary<int, ShaderContainerInfo> result = new();
        for (uint member = 0; member < placement.Map.NumShaders; member++)
        {
            long offset = placement.Map.ShaderIndicesOffset + member;
            if (offset < 0 || offset >= library.ShaderIndices.Length)
            {
                continue;
            }
            int shaderIndex = (int)library.ShaderIndices[offset];
            if (shaderIndex < 0 || shaderIndex >= library.ShaderEntries.Length || shaderIndex >= library.ShaderCount)
            {
                continue;
            }
            Record? truth = byResourceIndex.TryGetValue((int)member, out List<Record>? exact) && exact.Count > 0
                ? exact[0]
                : member < ordered.Count ? ordered[(int)member] : null;

            result[shaderIndex] = new ShaderContainerInfo
            {
                ContainerKey = containerKey,
                MaterialName = materialName,
                ShaderMapHash = target.ShaderMapHash,
                ShaderTypeHash = truth?.ShaderTypeHash ?? string.Empty,
                ShaderTypeName = Named(truth?.ShaderTypeName, truth?.ShaderTypeHash, shaderTypes.ResolveTypeName),
                VertexFactoryTypeHash = truth?.VertexFactoryTypeHash ?? string.Empty,
                VertexFactoryTypeName = Named(truth?.VertexFactoryTypeName, truth?.VertexFactoryTypeHash, vertexFactoryTypes.ResolveName),
                PipelineTypeHash = truth?.PipelineTypeHash ?? string.Empty,
                PipelineTypeName = Named(truth?.PipelineTypeName, truth?.PipelineTypeHash, pipelineTypes.ResolveName),
                PermutationId = truth?.PermutationId ?? -1,
                ResourceIndex = truth?.ResourceIndex ?? (int)member,
                Frequency = library.ShaderEntries[shaderIndex].Frequency,
                ShaderHash = library.ShaderHash(shaderIndex),
            };
        }
        return result;
    }

    /// <summary>
    /// The map's shaders in the order it states them: first the ones it owns directly, then each
    /// vertex factory's, then each pipeline's. That order is what a resource index counts along.
    /// </summary>
    private static List<Record> Ordered(FMaterialShaderMapContent content, Dictionary<string, string> names)
    {
        List<Record> result = new();
        int count = Math.Min(content.Shaders?.Length ?? 0, Math.Min(content.ShaderTypes?.Length ?? 0, content.ShaderPermutations?.Length ?? 0));
        for (int i = 0; i < count; i++)
        {
            FShader shader = content.Shaders![i];
            string typeHash = Prefer(Hash(shader.Type), Hash(content.ShaderTypes![i]));
            string vertexFactoryHash = VertexFactory(Hash(shader.VFType));
            result.Add(new Record(shader.ResourceIndex, typeHash, names.GetValueOrDefault(typeHash, string.Empty),
                vertexFactoryHash, names.GetValueOrDefault(vertexFactoryHash, string.Empty),
                content.ShaderPermutations![i], string.Empty, string.Empty));
        }

        foreach (FMeshMaterialShaderMap meshMap in content.OrderedMeshShaderMaps ?? [])
        {
            if (meshMap is null)
            {
                continue;
            }
            int meshCount = Math.Min(meshMap.Shaders?.Length ?? 0, Math.Min(meshMap.ShaderTypes?.Length ?? 0, meshMap.ShaderPermutations?.Length ?? 0));
            string meshVertexFactory = VertexFactory(Hash(meshMap.VertexFactoryTypeName));
            for (int i = 0; i < meshCount; i++)
            {
                FShader shader = meshMap.Shaders![i];
                if (shader is null)
                {
                    continue;
                }
                string typeHash = Prefer(Hash(shader.Type), Hash(meshMap.ShaderTypes![i]));
                string vertexFactoryHash = Prefer(VertexFactory(Hash(shader.VFType)), meshVertexFactory);
                result.Add(new Record(shader.ResourceIndex, typeHash, names.GetValueOrDefault(typeHash, string.Empty),
                    vertexFactoryHash, names.GetValueOrDefault(vertexFactoryHash, string.Empty),
                    meshMap.ShaderPermutations![i], string.Empty, string.Empty));
            }
        }

        foreach (FShaderPipeline pipeline in content.ShaderPipelines ?? [])
        {
            if (pipeline is null)
            {
                continue;
            }
            int pipelineCount = Math.Min(pipeline.Shaders?.Length ?? 0, pipeline.PermutationIds?.Length ?? 0);
            string pipelineHash = Hash(pipeline.TypeName);
            for (int i = 0; i < pipelineCount; i++)
            {
                FShader shader = pipeline.Shaders![i];
                if (shader is null)
                {
                    continue;
                }
                string typeHash = Hash(shader.Type);
                string vertexFactoryHash = VertexFactory(Hash(shader.VFType));
                result.Add(new Record(shader.ResourceIndex, typeHash, names.GetValueOrDefault(typeHash, string.Empty),
                    vertexFactoryHash, names.GetValueOrDefault(vertexFactoryHash, string.Empty),
                    pipeline.PermutationIds![i], pipelineHash, names.GetValueOrDefault(pipelineHash, string.Empty)));
            }
        }
        return result;
    }

    /// <summary>
    /// Hash to name, as the map itself states it: every layout the map depends on is named in its
    /// pointer table, and a type's hash is the hash of its name.
    /// </summary>
    private static Dictionary<string, string> Names(FMaterialShaderMap map)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (map.PointerTable is not FShaderMapPointerTable table || table.TypeDependencies is null)
        {
            return result;
        }
        foreach (FTypeLayoutDesc dependency in table.TypeDependencies)
        {
            string? name = dependency.Name?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.TryAdd(HashedNamesResolver.HashName(name!), name!);
            }
        }
        return result;
    }

    private static string Named(string? stated, string? hash, Func<string, string?> index)
    {
        if (!string.IsNullOrWhiteSpace(stated))
        {
            return stated!;
        }
        if (string.IsNullOrWhiteSpace(hash))
        {
            return string.Empty;
        }
        return index(hash!) ?? string.Empty;
    }

    private static string Hash(FHashedName hashedName) => hashedName.Hash != 0 ? hashedName.Hash.ToString("X16") : string.Empty;

    private static string VertexFactory(string hash) =>
        string.IsNullOrWhiteSpace(hash) || string.Equals(hash, EmptyVertexFactoryHash, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : hash;

    private static string Prefer(string preferred, string fallback) => preferred.Length > 0 ? preferred : fallback;

    private static string Shorten(string hash) =>
        string.IsNullOrWhiteSpace(hash) ? "UNKNOWN"
        : hash.Length <= ContainerKeyHashLength ? hash
        : hash[..ContainerKeyHashLength];
}
