namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 146 — backfill blank `ShaderTypeName` / `VertexFactoryTypeName`
// / `PipelineTypeName` on every `ShaderContainerInfo` from the
// corresponding hash-to-name indexes loaded in Pass145.
//
// The cook stores hashes for ShaderType/VertexFactoryType/PipelineType
// but may not preserve the string names alongside (export-side
// HashedNamesResolver covers ShaderType but not the sister types). After
// Pass130 reads `.stableinfo.json` into `state.ContainerByShaderIndex`
// (and `ContainersByMapAndIndex`) those name slots can be empty. Pass200
// surfaces them as `// VertexFactoryType:` / `// PipelineType:` comment
// lines in the emitted `.shader` and the per-variant filename
// (`Vertex_<ShaderType>_<VF>_PERM<id>_<hash>.hlsl`); blank → no segment.
// This pass fills from the indexes when available.
//
// ShaderTypeName backfill is essential when an OLDER export wrote
// empty names into the cached UnifiedShaderMetadata.json (e.g. when
// the export-side HashedNamesResolver couldn't find the UE source).
// Pass180's per-shader seed lookup also resolves names but doesn't
// write back to the container — so filenames stay nameless even
// after Pass180 succeeds. The load-side backfill below ensures the
// filename builder in Pass200 sees the resolved class name regardless
// of whether the export side managed to bake it in.
internal static class Pass146_BackfillContainerNames
{
    public static void DoPass(PipelineState state)
    {
        int shaderTypeFilled = 0, vfFilled = 0, pipeFilled = 0;
        bool stHashToNameLoaded = state.ShaderTypeSeedRegistry.HashToNameCount > 0;
        if (!stHashToNameLoaded
            && state.VertexFactoryTypeNameIndex.Count == 0
            && state.PipelineTypeNameIndex.Count == 0)
        {
            state.Log($"    Pass146: indexes empty (st={state.ShaderTypeSeedRegistry.HashToNameCount} vf={state.VertexFactoryTypeNameIndex.Count} pipeline={state.PipelineTypeNameIndex.Count}) — nothing to backfill.");
            return;
        }

        // Track which hashes failed to resolve so we can surface coverage gaps
        // at the end of the pass (same diagnostic pattern as Pass180).
        System.Collections.Generic.HashSet<string> unknownStHashes = new(System.StringComparer.OrdinalIgnoreCase);
        System.Collections.Generic.HashSet<string> unknownVfHashes = new(System.StringComparer.OrdinalIgnoreCase);
        System.Collections.Generic.HashSet<string> unknownPipelineHashes = new(System.StringComparer.OrdinalIgnoreCase);

        foreach (ShaderContainerInfo info in state.ContainerByShaderIndex.Values)
        {
            if (string.IsNullOrEmpty(info.ShaderTypeName) && !string.IsNullOrEmpty(info.ShaderTypeHash) && stHashToNameLoaded)
            {
                string? name = state.ShaderTypeSeedRegistry.ResolveTypeName(info.ShaderTypeHash);
                if (!string.IsNullOrEmpty(name))
                {
                    info.ShaderTypeName = name!;
                    shaderTypeFilled++;
                }
                else
                {
                    unknownStHashes.Add(info.ShaderTypeHash);
                }
            }
            if (string.IsNullOrEmpty(info.VertexFactoryTypeName) && !string.IsNullOrEmpty(info.VertexFactoryTypeHash))
            {
                string? name = state.VertexFactoryTypeNameIndex.ResolveName(info.VertexFactoryTypeHash);
                if (!string.IsNullOrEmpty(name))
                {
                    info.VertexFactoryTypeName = name!;
                    vfFilled++;
                }
                else
                {
                    unknownVfHashes.Add(info.VertexFactoryTypeHash);
                }
            }
            if (string.IsNullOrEmpty(info.PipelineTypeName) && !string.IsNullOrEmpty(info.PipelineTypeHash))
            {
                string? name = state.PipelineTypeNameIndex.ResolveName(info.PipelineTypeHash);
                if (!string.IsNullOrEmpty(name))
                {
                    info.PipelineTypeName = name!;
                    pipeFilled++;
                }
                else
                {
                    unknownPipelineHashes.Add(info.PipelineTypeHash);
                }
            }
        }

        // Per-map mirror — same backfill against the authoritative copies.
        foreach (System.Collections.Generic.Dictionary<int, ShaderContainerInfo> perMap in state.ContainersByMapAndIndex.Values)
        {
            foreach (ShaderContainerInfo info in perMap.Values)
            {
                if (string.IsNullOrEmpty(info.ShaderTypeName) && !string.IsNullOrEmpty(info.ShaderTypeHash) && stHashToNameLoaded)
                {
                    string? name = state.ShaderTypeSeedRegistry.ResolveTypeName(info.ShaderTypeHash);
                    if (!string.IsNullOrEmpty(name)) info.ShaderTypeName = name!;
                }
                if (string.IsNullOrEmpty(info.VertexFactoryTypeName) && !string.IsNullOrEmpty(info.VertexFactoryTypeHash))
                {
                    string? name = state.VertexFactoryTypeNameIndex.ResolveName(info.VertexFactoryTypeHash);
                    if (!string.IsNullOrEmpty(name)) info.VertexFactoryTypeName = name!;
                }
                if (string.IsNullOrEmpty(info.PipelineTypeName) && !string.IsNullOrEmpty(info.PipelineTypeHash))
                {
                    string? name = state.PipelineTypeNameIndex.ResolveName(info.PipelineTypeHash);
                    if (!string.IsNullOrEmpty(name)) info.PipelineTypeName = name!;
                }
            }
        }

        state.Log($"    Pass146: backfilled ShaderTypeName={shaderTypeFilled}, VertexFactoryTypeName={vfFilled}, PipelineTypeName={pipeFilled} container(s).");
        if (unknownStHashes.Count > 0)
        {
            state.Log($"    Pass146 unknown ShaderType hashes: {unknownStHashes.Count} (TPK dumper's IMPLEMENT_*_SHADER_TYPE scan missed these).");
        }
        if (unknownVfHashes.Count > 0)
        {
            state.Log($"    Pass146 unknown VertexFactoryType hashes: {unknownVfHashes.Count} (generator's IMPLEMENT_VERTEX_FACTORY_TYPE scan missed these — likely game-specific factories).");
        }
        if (unknownPipelineHashes.Count > 0)
        {
            state.Log($"    Pass146 unknown PipelineType hashes: {unknownPipelineHashes.Count} (generator's IMPLEMENT_SHADERPIPELINE_TYPE_* scan missed these).");
        }
    }
}
