namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Pass 146 — backfill blank `VertexFactoryTypeName` / `PipelineTypeName`
// on every `ShaderContainerInfo` from the corresponding hash-to-name
// indexes loaded in Pass145.
//
// The cook stores hashes for ShaderType/VertexFactoryType/PipelineType
// but may not preserve the string names alongside (export-side
// HashedNamesResolver covers ShaderType but not the sister types). After
// Pass130 reads `.stableinfo.json` into `state.ContainerByShaderIndex`
// (and `ContainersByMapAndIndex`) those name slots can be empty. Pass200
// surfaces them as `// VertexFactoryType:` / `// PipelineType:` comment
// lines in the emitted `.shader`; blank → blank line. This pass fills
// from the indexes when available.
//
// ShaderTypeName itself is filled later (Pass180's per-shader logic uses
// `state.ShaderTypeSeedRegistry.ResolveTypeName` directly via
// TryLookupWithFallback). The VF/Pipeline slots aren't touched there
// because no per-class seed exists for them — only the name lookup.
internal static class Pass146_BackfillContainerNames
{
    public static void DoPass(PipelineState state)
    {
        int vfFilled = 0, pipeFilled = 0;
        if (state.VertexFactoryTypeNameIndex.Count == 0 && state.PipelineTypeNameIndex.Count == 0)
        {
            state.Log($"    Pass146: indexes empty (vf={state.VertexFactoryTypeNameIndex.Count} pipeline={state.PipelineTypeNameIndex.Count}) — nothing to backfill.");
            return;
        }

        // Track which hashes failed to resolve so we can surface coverage gaps
        // at the end of the pass (same diagnostic pattern as Pass180).
        System.Collections.Generic.HashSet<string> unknownVfHashes = new(System.StringComparer.OrdinalIgnoreCase);
        System.Collections.Generic.HashSet<string> unknownPipelineHashes = new(System.StringComparer.OrdinalIgnoreCase);

        foreach (ShaderContainerInfo info in state.ContainerByShaderIndex.Values)
        {
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

        state.Log($"    Pass146: backfilled VertexFactoryTypeName={vfFilled}, PipelineTypeName={pipeFilled} container(s).");
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
