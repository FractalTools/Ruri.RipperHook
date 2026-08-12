namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

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
