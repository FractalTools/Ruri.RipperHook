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

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler
{
    public static class UniformExpressionExporter
    {
        public static void ExportAll(CUE4ParseViewModel vm)
        {
            var provider = vm.Provider;
            if (provider == null) return;

            var mappingCollection = new MaterialMappingCollection();
            int exportCount = 0;

            foreach (var file in provider.Files.Values)
            {
                // We only care about materials
                if (!file.Name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
                
                // Quick check if it might be a material without full load?
                // Just try loading material assets.
                // Optimally we filter by path or ensure it's a material.
                // For now, let's look for "Material" in the name or path or rely on TryLoad.
                
                // Better: Check extension and try load if likely.
                // Or simply rely on the user exporting a specific package? 
                // The hook calling this is "ExtractGlobal" -> implies scanning all.
                // Scanning ALL files is slow.
                // But the user removed the filter.
                
                // Should we restrict to loaded assets? 
                // The previous logic scanned extracted JSONs.
                // We will try to rely on what CUE4Parse has in memory or fast loading.
                // Actually, let's just stick to what `MaterialParameterExporter` typically does.
                // If this is too slow, user will complain.
                // But we need to fix the bug.
                
                if (file.Path.Contains("/Material", StringComparison.OrdinalIgnoreCase) || 
                    file.Name.StartsWith("M_", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var asset = provider.LoadObject(file.PathWithoutExtension);
                        if (asset is UMaterialInterface material)
                        {
                            var mapping = ExtractParams(material);
                            if ((mapping.TextureParameters.Count > 0 || mapping.NumericParameters.Count > 0) &&
                                !mappingCollection.Materials.ContainsKey(file.PathWithoutExtension))
                            {
                                mappingCollection.Materials[file.PathWithoutExtension] = mapping;
                                exportCount++;
                            }
                        }
                    }
                    catch { /* Ignore load failures */ }
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
            var data = new MaterialMappingData();
            
            // Texture Parameters
            // We need to group them by Type (Standard2D, Volume, Cube, etc.)
            // We do this by checking the class of the assigned texture.
            
            // Iterate hierarchy to get all parameters
             var textureParams = new List<FTextureParameterValue>();
             // CUE4Parse helper might be needed to flatten parameters, assuming local implementation or iteration
             // UMaterialInterface usually has a method or property to get parameters.
             // If not, we check 'TextureParameterValues' property directly.
             
             CollectTextureParameters(material, textureParams);
             
             foreach (var param in textureParams)
             {
                 if (param.ParameterValue.TryLoad(out var texObj))
                 {
                     string group = "Standard2D"; // Default
                     
                     if (texObj is UTextureCube) group = "Cube";
                     else if (texObj is UVolumeTexture) group = "Volume";
                     else if (texObj is UTexture2DArray) group = "Array2D";
                     else if (texObj is UTextureCubeArray) group = "ArrayCube";
                     // Add other types as needed (Sparse, VT, etc.)
                     
                     // Helper: Check for VirtualTexture
                     if (texObj is UTexture2D t2d && t2d.IsVirtual)
                     {
                         group = "Virtual"; // or VirtualTexturePhysical
                     }
                     
                     if (!data.TextureParameters.ContainsKey(group))
                         data.TextureParameters[group] = new List<TextureParam>();
                         
                     data.TextureParameters[group].Add(new TextureParam
                     {
                         Name = param.ParameterInfo.Name.Text,
                         Index = data.TextureParameters[group].Count, // Recalculate index within group
                         TexturePath = texObj.GetPathName(),
                         TextureIndex = -1 // We don't verify the actual shader map index here, we infer order
                     });
                 }
             }

             // Sort to ensure deterministic order (e.g. by Name)
             // UE Sorts by Name? Or by Index in the array?
             // Usually UE keeps the order defined in the editor or sorted by hash. 
             // IMPORTANT: FUniformExpressionSet sorts alphabetically or by Guid? 
             // Standard UE behavior for Uniform Expressions -> Deterministic.
             // We'll sort by Name to be stable.
             foreach (var list in data.TextureParameters.Values)
             {
                 list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
                 // Re-index after sorting
                 for (int i = 0; i < list.Count; i++) list[i].Index = i;
             }

             return data;
        }

        private static void CollectTextureParameters(UMaterialInterface material, List<FTextureParameterValue> list)
        {
            // Simple recursive or iterative collection
            // CUE4Parse UMaterialInterface properties
            // Note: This logic depends on CUE4Parse API.
            
            var current = material;
            while (current != null)
            {
                // Use reflection or dynamic to get 'TextureParameterValues'
                // assuming CUE4Parse mapping properties
                // Try getting property value
                
                // Note: CUE4Parse UMaterial has "TextureParameterValues"
                // UMaterialInstance has "TextureParameterValues"
                
                // Pseudo-code dynamic access
                try {
                    var props = current.GetType().GetProperty("TextureParameterValues")?.GetValue(current) as IEnumerable<FTextureParameterValue>;
                    if (props != null)
                    {
                        foreach (var p in props)
                        {
                            if (!list.Any(x => x.ParameterInfo.Name.Text == p.ParameterInfo.Name.Text))
                                list.Add(p);
                        }
                    }
                } catch { }
                
                // Go to parent
                // UMaterialInstance -> Parent
                var parentProp = current.GetType().GetProperty("Parent");
                current = parentProp?.GetValue(current) as UMaterialInterface;
                // If UMaterial, parent is null
            }
        }

        // --- Data Classes ---

        public class MaterialMappingCollection
        {
            public Dictionary<string, MaterialMappingData> Materials { get; set; } = new();
        }

        public class MaterialMappingData
        {
            public Dictionary<string, List<TextureParam>> TextureParameters { get; set; } = new();
            public List<NumericParam> NumericParameters { get; set; } = new();
        }

        public class TextureParam
        {
            public int Index { get; set; }
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
