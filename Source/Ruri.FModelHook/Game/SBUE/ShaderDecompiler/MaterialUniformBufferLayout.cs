using System;
using System.Collections.Generic;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// Replays FUniformExpressionSet::CreateBufferStruct() so an SRT ResourceIndex can
// be mapped back to the canonical `Material` uniform-buffer member name.
internal sealed class MaterialUniformBufferLayout
{
    private readonly List<string> _resourceMemberNames;
    private readonly Dictionary<string, string> _typedSlotByAuthorName;

    public MaterialUniformBufferLayout(MaterialResourceCounts counts)
    {
        _resourceMemberNames = BuildResourceMemberNames(counts);
        _typedSlotByAuthorName = BuildAuthorIndex(counts);
    }

    public bool TryResolveAuthorName(string authorName, out string typedSlot)
        => _typedSlotByAuthorName.TryGetValue(authorName, out typedSlot!);

    public string? ResolveResourceName(SrtRecord record)
    {
        int idx = record.ResourceIndex;
        if (idx < 0 || idx >= _resourceMemberNames.Count)
        {
            return null;
        }

        return $"Material_{_resourceMemberNames[idx]}";
    }

    public IReadOnlyList<string> ResourceMemberNames => _resourceMemberNames;

    private static List<string> BuildResourceMemberNames(MaterialResourceCounts counts)
    {
        List<string> result = new();
        AppendTextureSamplerPairs(result, "Texture2D", counts.Standard2D, counts.Standard2DAuthorNames);
        AppendTextureSamplerPairs(result, "TextureCube", counts.Cube, counts.CubeAuthorNames);
        AppendTextureSamplerPairs(result, "Texture2DArray", counts.Array2D, counts.Array2DAuthorNames);
        AppendTextureSamplerPairs(result, "TextureCubeArray", counts.ArrayCube, counts.ArrayCubeAuthorNames);
        AppendTextureSamplerPairs(result, "VolumeTexture", counts.Volume, counts.VolumeAuthorNames);
        AppendTextureSamplerPairs(result, "ExternalTexture", counts.External, counts.ExternalAuthorNames);

        if (counts.VirtualTextureStackLayerCounts != null)
        {
            AppendVirtualTextureStacks(result, counts.VirtualTextureStackLayerCounts);
        }
        else if (counts.TotalResourceCount is int total)
        {
            // VTStacks not available (older serialization / IoStore cooks
            // where CUE4Parse doesn't surface VTStacks). Infer the slot
            // count from `Resources.Num()` and emit anonymous placeholders
            // that consume the SAME number of slots the engine emitted —
            // crucial so the post-VT `VirtualTexturePhysical`,
            // `Wrap_/Clamp_WorldGroupSettings` resolve at the correct
            // ResourceIndex.
            //
            // Engine `MaterialUniformExpressions.cpp:473-486` emits 2 or
            // 3 resources per stack (PageTable0 + optional PageTable1 if
            // NumLayers>4 + PageTableIndirection). Without the actual
            // NumLayers we can't disambiguate the per-stack split, so we
            // emit one anonymous slot per remaining resource and leave
            // the actual VirtualTexturePhysical / Wrap / Clamp lookups
            // correctly aligned downstream.
            int textureSamplerPairsConsumed = 2 * (counts.Standard2D + counts.Cube + counts.Array2D + counts.ArrayCube + counts.Volume + counts.External);
            int virtualPhysicalConsumed = 2 * counts.Virtual;
            int fixedTrailingSamplers = 2;
            int vtSlotCount = total - textureSamplerPairsConsumed - virtualPhysicalConsumed - fixedTrailingSamplers;
            for (int i = 0; i < vtSlotCount; i++)
            {
                result.Add($"VTStackResource_{i}");
            }
        }

        AppendTextureSamplerPairs(result, "VirtualTexturePhysical", counts.Virtual, counts.VirtualAuthorNames);
        result.Add("Wrap_WorldGroupSettings");
        result.Add("Clamp_WorldGroupSettings");
        return result;
    }

    private static void AppendTextureSamplerPairs(List<string> result, string baseName, int count, IReadOnlyList<string?>? authorNames = null)
    {
        for (int i = 0; i < count; i++)
        {
            string? author = authorNames != null && i < authorNames.Count ? authorNames[i] : null;
            string sanitized = SanitizeHlslIdent(author);
            string textureName = string.IsNullOrEmpty(sanitized) ? $"{baseName}_{i}" : sanitized;
            result.Add(textureName);
            result.Add($"{textureName}Sampler");
        }
    }

    private static string SanitizeHlslIdent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "None", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[raw.Length];
        int written = 0;
        foreach (char c in raw)
        {
            char ch = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_';
            buffer[written++] = ch;
        }

        if (written == 0)
        {
            return string.Empty;
        }

        if (buffer[0] >= '0' && buffer[0] <= '9')
        {
            return "_" + new string(buffer[..written]);
        }

        return new string(buffer[..written]);
    }

    private static Dictionary<string, string> BuildAuthorIndex(MaterialResourceCounts counts)
    {
        Dictionary<string, string> index = new(StringComparer.Ordinal);
        Add(index, "Texture2D", counts.Standard2D, counts.Standard2DAuthorNames);
        Add(index, "TextureCube", counts.Cube, counts.CubeAuthorNames);
        Add(index, "Texture2DArray", counts.Array2D, counts.Array2DAuthorNames);
        Add(index, "TextureCubeArray", counts.ArrayCube, counts.ArrayCubeAuthorNames);
        Add(index, "VolumeTexture", counts.Volume, counts.VolumeAuthorNames);
        Add(index, "ExternalTexture", counts.External, counts.ExternalAuthorNames);
        Add(index, "VirtualTexturePhysical", counts.Virtual, counts.VirtualAuthorNames);
        return index;

        static void Add(Dictionary<string, string> idx, string baseName, int count, IReadOnlyList<string?>? authorNames)
        {
            if (authorNames == null)
            {
                return;
            }

            for (int i = 0; i < count && i < authorNames.Count; i++)
            {
                string sanitized = SanitizeHlslIdent(authorNames[i]);
                if (sanitized.Length == 0)
                {
                    continue;
                }

                idx[sanitized] = $"{baseName}_{i}";
                idx[sanitized + "Sampler"] = $"{baseName}_{i}Sampler";
            }
        }
    }

    private static void AppendVirtualTextureStacks(List<string> result, IReadOnlyList<int>? layerCountsPerStack)
    {
        if (layerCountsPerStack == null)
        {
            return;
        }

        for (int i = 0; i < layerCountsPerStack.Count; i++)
        {
            result.Add($"VirtualTexturePageTable0_{i}");
            if (layerCountsPerStack[i] > 4)
            {
                result.Add($"VirtualTexturePageTable1_{i}");
            }
            result.Add($"VirtualTexturePageTableIndirection_{i}");
        }
    }

    public sealed record MaterialResourceCounts(
        int Standard2D,
        int Cube,
        int Array2D,
        int ArrayCube,
        int Volume,
        int External,
        int Virtual,
        IReadOnlyList<int>? VirtualTextureStackLayerCounts,
        int? TotalResourceCount = null,
        IReadOnlyList<string?>? Standard2DAuthorNames = null,
        IReadOnlyList<string?>? CubeAuthorNames = null,
        IReadOnlyList<string?>? Array2DAuthorNames = null,
        IReadOnlyList<string?>? ArrayCubeAuthorNames = null,
        IReadOnlyList<string?>? VolumeAuthorNames = null,
        IReadOnlyList<string?>? ExternalAuthorNames = null,
        IReadOnlyList<string?>? VirtualAuthorNames = null);
}
