using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using FModel.ViewModels;
using FModel.Settings;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.FileProvider.Vfs;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler
{
    [GameHook("UE_ShaderDecompiler")]
    public class UE_ShaderDecompiler_Hook : RuriHook
    {
        private static bool _hasExtractedMappings = false;
        private static readonly object _mappingLock = new object();

        // Use RetargetMethod to safely inject C# logic before the original method and fall through (IsReturn = false)
        // Positional args: Type source, string methodName, bool isBefore, bool isReturn
        [RetargetMethod(typeof(CUE4ParseViewModel), "ExportData", true, false)]
        public static void ExportData_Hook(CUE4ParseViewModel self, GameFile entry, bool updateUi)
        {
            if (entry == null) return;

            // Only trigger on Shader Bytecode Library export
            if (entry.Extension.Equals("ushaderbytecode", StringComparison.OrdinalIgnoreCase))
            {
                // 1. Export Shader Library (.ushaderlib)
                var libraryBytes = ShaderArchiveExporter.SaveShaderLibrary(entry);
                if (libraryBytes != null)
                {
                    string path = Path.Combine(UserSettings.Default.RawDataDirectory, UserSettings.Default.KeepDirectoryStructure ? entry.PathWithoutExtension : entry.NameWithoutExtension).Replace('\\', '/') + ".ushaderlib";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, libraryBytes);
                        HookLogger.LogSuccess($"[+] Exported ShaderLibrary: {path}");
                    }
                    catch (Exception ex)
                    {
                        HookLogger.LogFailure($"Failed to save .ushaderlib: {ex.Message}");
                    }
                }

                // 2. Extract Global Shader Mappings (Once)
                // User requested global unique mapping export to proper directory.
                // We use double-check locking to ensure it runs only once.
                if (!_hasExtractedMappings)
                {
                    lock (_mappingLock)
                    {
                        if (!_hasExtractedMappings)
                        {
                            try 
                            {
                                ExtractGlobalShaderMappings(self);
                                _hasExtractedMappings = true; 
                            }
                            catch (Exception ex)
                            {
                                HookLogger.LogFailure($"[UE_ShaderDecompiler] Failed to extract global mappings: {ex.Message}");
                            }

                            // Extract parameter mappings from material Properties
                            try
                            {
                                MaterialParameterExporter.ExportAll(self);
                            }
                            catch (Exception ex)
                            {
                                HookLogger.LogFailure($"[UE_ShaderDecompiler] Failed to export material parameters: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }

        private static void ExtractGlobalShaderMappings(CUE4ParseViewModel self)
        {
            var provider = self.Provider;
            if (provider == null) return;

            HookLogger.Log("[UE_ShaderDecompiler] Starting Global IoStore Shader Map Extraction...");

            var mapping = new Dictionary<string, List<string>>();
            int totalMatches = 0;

            var readers = provider.MountedVfs.Concat(provider.UnloadedVfs); 
            
            foreach (var reader in readers)
            {
                if (reader is IoStoreReader ioReader)
                {
                    if (ioReader.ContainerHeader == null) continue;

                    var header = ioReader.ContainerHeader;
                    var packageIds = header.PackageIds;
                    var storeEntries = header.StoreEntries;

                    if (packageIds == null || storeEntries == null || packageIds.Length != storeEntries.Length)
                        continue;

                    for (int i = 0; i < packageIds.Length; i++)
                    {
                        var entry = storeEntries[i];
                        if (entry.ShaderMapHashes != null && entry.ShaderMapHashes.Length > 0)
                        {
                            var packageId = packageIds[i];
                            
                            // Resolve PackageId to Name
                            if (ioReader.PackageIdIndex.TryGetValue(packageId, out var gameFile))
                            {
                                var packageName = gameFile.PathWithoutExtension; 
                                var hashes = entry.ShaderMapHashes.Select(h => h.ToString()).ToList();
                                
                                mapping[packageName] = hashes;
                                totalMatches++;
                            }
                        }
                    }
                }
            }

            if (mapping.Count > 0)
            {
                // Save to RawDataDirectory/{ProjectName}/ShaderMappings.json (Game Export Root)
                // Default.RawDataDirectory usually maps to "Output/Exports"
                // Provider.ProjectName usually maps to the GameName
                var projectName = self.Provider.ProjectName ?? "UnknownProject";
                var outputPath = Path.Combine(UserSettings.Default.RawDataDirectory, projectName, "ShaderMappings.json");
                
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

                var json = JsonConvert.SerializeObject(mapping, Formatting.Indented);
                File.WriteAllBytes(outputPath, System.Text.Encoding.UTF8.GetBytes(json));
                
                HookLogger.LogSuccess($"[UE_ShaderDecompiler] Extracted {totalMatches} mappings to {outputPath}");
            }
            else
            {
                HookLogger.LogWarning("[UE_ShaderDecompiler] No shader mappings found in mounted IoStores.");
            }
        }
    }
}
