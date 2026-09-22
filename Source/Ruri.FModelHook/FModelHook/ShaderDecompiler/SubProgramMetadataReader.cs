using System;
using System.Collections.Generic;
using System.Linq;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class SubProgramMetadataReader
{
    public static SerializedProgramData Read(
        UnrealShaderParser.UnrealMetadata? runtimeMetadata,
        MaterialSymbolSource? materialSource,
        EngineUbMetadataRegistry? engineUbRegistry = null,
        Action<string>? logMiss = null)
    {
        SerializedProgramData metadata = RuntimeSymbolReader.Read(runtimeMetadata, materialSource?.MaterialLayout, engineUbRegistry, logMiss);
        if (materialSource == null)
        {
            return metadata;
        }

        foreach (ConstantBufferParameter cb in materialSource.Metadata.ConstantBufferParameters)
        {
            if (!metadata.ConstantBufferParameters.Any(existing => string.Equals(existing.Name, cb.Name, StringComparison.Ordinal)))
            {
                metadata.ConstantBufferParameters.Add(cb);
            }
        }

        metadata.EntryPoint = materialSource.Metadata.EntryPoint;
        metadata.DebugName = materialSource.Metadata.DebugName;
        metadata.UsedMaterials = new List<string>(materialSource.Metadata.UsedMaterials);
        return metadata;
    }
}
