using System.IO;
using Newtonsoft.Json;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.ShaderDecompiler;

internal static class Pass080_WriteUnifiedMetadataJson
{
    public static void DoPass(ExportPipelineState state)
    {
        var output = state.Root;
        if (output.MaterialInterfaces.Count == 0
            && output.PackageShaderMapHashes.Count == 0
            && output.NiagaraShaderMapHashes.Count == 0
            && output.ShaderCodeArchives.Count == 0)
        {
            HookLogger.LogWarning("[Pass080_WriteUnifiedMetadataJson] No verified shader metadata found to export.");
            return;
        }

        var provider = state.Provider;
        if (provider == null) return;

        output.GameVersionEnum = provider.Versions?.Game.ToString() ?? string.Empty;
        output.CacheFormatVersion = UnifiedShaderMetadataRoot.CurrentCacheFormatVersion;

        string outputRoot = !string.IsNullOrEmpty(state.ProjectOutputRoot)
            ? state.ProjectOutputRoot
            : Path.Combine(Path.GetDirectoryName(state.ExportBasePath) ?? ".", provider.ProjectName ?? "UnknownProject");
        string outputPath = Path.Combine(outputRoot, "UnifiedShaderMetadata.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string tempPath = outputPath + ".tmp";
        var serializer = JsonSerializer.Create(new JsonSerializerSettings { Formatting = Formatting.Indented });
        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var streamWriter = new StreamWriter(fileStream))
        using (var jsonWriter = new JsonTextWriter(streamWriter) { Formatting = Formatting.Indented })
        {
            serializer.Serialize(jsonWriter, output);
        }
        if (File.Exists(outputPath))
        {
            File.Replace(tempPath, outputPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, outputPath);
        }

        state.UnifiedMetadataWritten = true;
        HookLogger.LogSuccess($"[Pass080_WriteUnifiedMetadataJson] Wrote unified metadata: {output.MaterialInterfaces.Count} materials, {output.PackageShaderMapHashes.Count} package->shader-map associations, {output.NiagaraShaderMapHashes.Count} Niagara hash bridges, {output.ShaderCodeArchives.Count} archives.");
    }
}
