using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Ruri.Hook;
using FModel.ViewModels;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler
{
    public static class UniformExpressionExporter
    {
        public enum EMaterialTextureParameterType
        {
            Standard2D = 0,
            Cube = 1,
            Array2D = 2,
            ArrayCube = 3,
            Volume = 4,
            Virtual = 5,
            SparseVolume = 6,
            Count
        }

        public static void ExportAll(CUE4ParseViewModel vm)
        {
            var provider = vm.Provider;
            if (provider == null) return;
            
            // Check if ReadShaderMaps is enabled
            if (provider is AbstractFileProvider abstractProv)
            {
                 HookLogger.LogWarning($"[UniformExpressionExporter] Provider.ReadShaderMaps = {abstractProv.ReadShaderMaps}");
                 if (!abstractProv.ReadShaderMaps)
                 {
                     abstractProv.ReadShaderMaps = true;
                     HookLogger.LogWarning($"[UniformExpressionExporter] Force enabled ReadShaderMaps.");
                 }
            }

            var mappingCollection = new MaterialMappingCollection();
            int exportCount = 0;

            foreach (var file in provider.Files.Values)
            {
                if (!file.Name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;

                if (file.Path.Contains("/Material", StringComparison.OrdinalIgnoreCase) || 
                    file.Name.StartsWith("M_", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var asset = provider.LoadPackageObject(file.PathWithoutExtension);
                        if (asset is UMaterialInterface material)
                        {
                            var mapping = ExtractParams(material);
                            if (mapping != null && (mapping.TextureParameters.Count > 0 || mapping.NumericParameters.Count > 0))
                            {
                                if (!mappingCollection.Materials.ContainsKey(file.PathWithoutExtension))
                                {
                                    mappingCollection.Materials[file.PathWithoutExtension] = mapping;
                                    exportCount++;
                                }
                            }
                        }
                    }
                    catch (Exception ex) 
                    {
                        // HookLogger.LogError($"Failed to export material {file.Name}: {ex.Message}");
                    }
                }
            }
            if (exportCount > 0)
            {
                var projectName = vm.Provider.ProjectName ?? "UnknownProject";
                var outputPath = Path.Combine(FModel.Settings.UserSettings.Default.RawDataDirectory, projectName, "MaterialParameterMappings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                
                var json = JsonConvert.SerializeObject(mappingCollection, Formatting.Indented);
                File.WriteAllText(outputPath, json);
                
                HookLogger.LogSuccess($"[UniformExpressionExporter] Exported mappings for {exportCount} materials.");
            }
        }

        private static MaterialMappingData ExtractParams(UMaterialInterface material)
        {
            // Debug: Check if resources exist
            // HookLogger.LogWarning($"[ExtractParams] Processing {material.Name}...");
            
            // Strategy 1: FMaterialShaderMap (via LoadedMaterialResources)
            if (material.LoadedMaterialResources != null && material.LoadedMaterialResources.Count > 0)
            {
                HookLogger.LogWarning($"[Debug] {material.Name} has {material.LoadedMaterialResources.Count} LoadedMaterialResources.");
                
                // Iterate resources to find one with a valid map
                foreach (var resource in material.LoadedMaterialResources)
                {
                    if (resource.LoadedShaderMap != null)
                    {
                        var data = ExtractFromShaderMap(resource.LoadedShaderMap);
                        if (data != null) return data;
                    }
                    else
                    {
                         HookLogger.LogWarning($"  - Resource has NULL LoadedShaderMap.");
                    }
                }
            }
            else
            {
                 // HookLogger.LogWarning($"  - No LoadedMaterialResources found.");
            }
// ...

            // Strategy 2: CachedExpressionData Fallback
            if (material.CachedExpressionData != null)
            {
                // HookLogger.LogWarning($"  - Fallback to CachedExpressionData.");
                return ExtractFromCachedData(material);
            }

            return null;
        }

        private static MaterialMappingData ExtractFromShaderMap(FMaterialShaderMap shaderMap)
        {
            HookLogger.LogWarning($"[ExtractFromShaderMap] Attempting extraction...");
            
            var content = shaderMap.Content as FMaterialShaderMapContent;
            if (content == null) 
            {
                HookLogger.LogFailure($"[ExtractFromShaderMap] Content is NOT FMaterialShaderMapContent. Type: {shaderMap.Content?.GetType().Name}");
                return null;
            }

            if (content.MaterialCompilationOutput == null) 
            {
                 HookLogger.LogFailure($"[ExtractFromShaderMap] MaterialCompilationOutput is NULL.");
                 return null;
            }

            if (content.MaterialCompilationOutput.UniformExpressionSet == null)
            {
                 HookLogger.LogFailure($"[ExtractFromShaderMap] UniformExpressionSet is NULL.");
                 return null;
            }

            var expressionSet = content.MaterialCompilationOutput.UniformExpressionSet;
            var data = new MaterialMappingData();

            // 1. Flatten parameters to create the "Layout"
            // The order here MUST match how CreateBufferStruct adds them.
            // In MaterialUniformExpressions.cpp: iterates types 0..N, then parameters 0..M
            var parameterLayout = new List<(string Name, FMaterialTextureParameterInfo Info)>();
            
            if (expressionSet.UniformTextureParameters != null)
            {
                for (int typeIndex = 0; typeIndex < expressionSet.UniformTextureParameters.Length; typeIndex++)
                {
                    var paramsForType = expressionSet.UniformTextureParameters[typeIndex];
                    if (paramsForType == null) continue;

                    for (int i = 0; i < paramsForType.Length; i++)
                    {
                        var paramInfo = paramsForType[i];
                        string paramName = paramInfo.ParameterInfo?.Name.Text ?? $"Unnamed_Tex_Type{typeIndex}_{i}";
                        parameterLayout.Add((paramName, paramInfo));
                    }
                }
            }

            if (parameterLayout.Count == 0) return data;

            // 2. Get Bindings from the first available shader
            // This tells us: "Struct Offset X maps to Register Y"
            Dictionary<int, int> offsetToRegister = new Dictionary<int, int>();
            
            if (content.OrderedMeshShaderMaps != null && content.OrderedMeshShaderMaps.Length > 0)
            {
                var meshMap = content.OrderedMeshShaderMaps[0];
                if (meshMap != null && meshMap.Shaders != null && meshMap.Shaders.Length > 0)
                {
                   HookLogger.LogWarning($"[UniformExpressionExporter] Scanning {meshMap.Shaders.Length} shaders for bindings...");

                   foreach (var shader in meshMap.Shaders)
                   {
                       if (shader == null) continue;
                       var bindings = shader.Bindings;
                       if (bindings == null) continue;

                       // Check standard ResourceParameters
                       var resources = bindings.ResourceParameters;
                       if (resources == null && bindings.Textures != null) resources = bindings.Textures;

                       if (resources != null && resources.Length > 0)
                       {
                           // HookLogger.LogWarning($"  - Shader has {resources.Length} ResourceParameters.");
                           foreach (var res in resources)
                           {
                               bool isTexture = res.BaseType == EUniformBufferBaseType.UBMT_TEXTURE || 
                                                res.BaseType == EUniformBufferBaseType.UBMT_SRV || 
                                                res.BaseType == EUniformBufferBaseType.UBMT_RDG_TEXTURE ||
                                                res.BaseType == EUniformBufferBaseType.UBMT_RDG_TEXTURE_SRV;
                               
                               if (isTexture) 
                               {
                                   if (!offsetToRegister.ContainsKey(res.ByteOffset))
                                   {
                                       offsetToRegister[res.ByteOffset] = res.BaseIndex;
                                       // HookLogger.LogWarning($"    Mapped Offset {res.ByteOffset} -> Register {res.BaseIndex}");
                                   }
                               }
                           }
                       }

                       // Check BindlessResourceParameters (UE5+)
                       var bindless = bindings.BindlessResourceParameters;
                       if (bindless != null && bindless.Length > 0)
                       {
                           // HookLogger.LogWarning($"  - Shader has {bindless.Length} BindlessResourceParameters.");
                           foreach (var res in bindless)
                           {
                               bool isTexture = res.BaseType == EUniformBufferBaseType.UBMT_TEXTURE || 
                                                res.BaseType == EUniformBufferBaseType.UBMT_SRV || 
                                                res.BaseType == EUniformBufferBaseType.UBMT_RDG_TEXTURE ||
                                                res.BaseType == EUniformBufferBaseType.UBMT_RDG_TEXTURE_SRV;
                               
                               if (isTexture)
                               {
                                    // Bindless usually uses GlobalConstantOffset? Or is ByteOffset still valid for the Uniform Struct?
                                    // Based on CUE4Parse: public ushort ByteOffset;
                                    // We'll assume ByteOffset maps to the Uniform Struct.
                                   if (!offsetToRegister.ContainsKey(res.ByteOffset))
                                   {
                                       // For bindless, BaseIndex might not be the register index in the same way?
                                       // But let's try.
                                       // Actually, bindless resources don't use t# registers in the same way, 
                                       // they use indices into a global descriptor heap.
                                       // But if we are generating HLSL that uses t#, this might be tricky.
                                       // However, if the shader uses a uniform expression struct, it still needs to know where in the struct.
                                       
                                       // Wait, if it's bindless, we might not get a t# register.
                                       // But usually, standard material textures are bound as SRVs.
                                   }
                               }
                           }
                       }
                   }
                }
                else
                {
                    HookLogger.LogWarning($"[UniformExpressionExporter] meshMap.Shaders is empty or null.");
                }
            }
            else
            {
                HookLogger.LogWarning($"[UniformExpressionExporter] OrderedMeshShaderMaps is empty or null.");
            }

            // 3. Match Layout to Bindings
            int inferredStride = 8; 

            if (offsetToRegister.Count > 0)
            {
                HookLogger.LogSuccess($"[UniformExpressionExporter] using Method A (Exact Bindings). Found {offsetToRegister.Count} bindings.");
                for (int i = 0; i < parameterLayout.Count; i++)
                {
                    int expectedOffset = i * inferredStride;
                    int boundIndex = -1;

                    if (offsetToRegister.TryGetValue(expectedOffset, out int regIndex))
                    {
                        boundIndex = regIndex;
                    }

                     // Log if missing
                    if (boundIndex == -1)
                    {
                         HookLogger.LogWarning($"  [Warning] Param '{parameterLayout[i].Name}' at layout offset {expectedOffset} not found in bindings.");
                    }
                    else
                    {
                         // HookLogger.LogSuccess($"  Matched '{parameterLayout[i].Name}' -> {boundIndex}");
                    }

                    // Even if -1, we should probably add it but with -1 index? 
                    // Or precise mapper might fail.
                    // Let's add it if we found it.
                    if (boundIndex != -1)
                    {
                        data.TextureParameters.Add(new TextureParam
                        {
                            Name = parameterLayout[i].Name,
                            Index = boundIndex,
                            TextureIndex = parameterLayout[i].Info.TextureIndex,
                            TexturePath = null 
                        });
                    }
                }
            }
            else
            {
                HookLogger.LogWarning($"[UniformExpressionExporter] Method A failed. Fallback to Method B (Sequential).");
                int globalIndex = 0;
                foreach(var item in parameterLayout)
                {
                    data.TextureParameters.Add(new TextureParam
                    {
                        Name = item.Name,
                        Index = globalIndex++,
                        TextureIndex = item.Info.TextureIndex,
                        TexturePath = null
                    });
                }
            }

            return data;
        }

        private static MaterialMappingData ExtractFromCachedData(UMaterialInterface material)
        {
            var data = new MaterialMappingData();
            var cachedData = material.CachedExpressionData;
            if (cachedData == null) return null;

            FStructFallback[] runtimeEntries;
            if (cachedData.TryGetValue(out FStructFallback parameters, "Parameters"))
            {
                if (!parameters.TryGetAllValues(out runtimeEntries, "RuntimeEntries")) return null;
            }
            else
            {
                 if (!cachedData.TryGetAllValues(out runtimeEntries, "RuntimeEntries")) return null;
            }

            if (runtimeEntries == null) return null;

            FMaterialParameterInfo[] textureInfos = null;
            int textureEntryIndex = -1;
            if (runtimeEntries.Length > 3) textureEntryIndex = 3;
            else if (runtimeEntries.Length > 2) textureEntryIndex = 2;

            if (textureEntryIndex != -1)
            {
                 var entry = runtimeEntries[textureEntryIndex];
                 if (!entry.TryGetValue(out textureInfos, "ParameterInfoSet"))
                 {
                     entry.TryGetValue(out textureInfos, "ParameterInfos");
                 }
            }

            if (textureInfos != null)
            {
                FSoftObjectPath[] textureValues = null;
                if (!cachedData.TryGetValue(out textureValues, "TextureValues"))
                {
                     if (parameters != null && parameters.TryGetValue(out textureValues, "TextureValues")) { }
                }

                if (textureValues != null && textureValues.Length == textureInfos.Length)
                {
                    int globalIndex = 0;

                    // Fallback: We can't know the exact order without shader, 
                    // BUT CachedData usually stores them in the order they were compiled/cooked.
                    // So we add them sequentially.
                    
                    for (int i = 0; i < textureInfos.Length; i++)
                    {
                        var info = textureInfos[i];
                        data.TextureParameters.Add(new TextureParam
                        {
                            Name = info.Name.Text,
                            Index = globalIndex++, 
                            TextureIndex = -1,
                            TexturePath = textureValues[i].AssetPathName.Text
                        });
                    }
                }
            }

            return data;
        }

        public class MaterialMappingCollection
        {
            public Dictionary<string, MaterialMappingData> Materials { get; set; } = new();
        }

        public class MaterialMappingData
        {
            // CHANGED: List<TextureParam> instead of Dictionary
            public List<TextureParam> TextureParameters { get; set; } = new();
            public List<NumericParam> NumericParameters { get; set; } = new();
        }

        public class TextureParam
        {
            public int Index { get; set; }     // Global Register Index (t#)
            public string Name { get; set; }
            public int TextureIndex { get; set; }
            public string TexturePath { get; set; }
        }

        public class NumericParam
        {
            public string Name { get; set; } 
            public string Type { get; set; }
            public string DefaultValue { get; set; }
        }
    }
}
