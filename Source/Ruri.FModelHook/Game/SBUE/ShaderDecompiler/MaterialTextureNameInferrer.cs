using System;
using System.Collections.Generic;
using Ruri.ShaderTools;
using Ruri.ShaderTools.Spirv;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class MaterialTextureNameInferrer
{
    private const ushort OpLoad = 61;
    private const ushort OpSampledImage = 86;

    public static int InferAndAppend(byte[] spirv, SerializedProgramData symbols)
    {
        if (spirv == null || spirv.Length < SpvOpCode.HeaderWordCount * 4)
        {
            return 0;
        }
        if (symbols.SamplerParameters.Count == 0)
        {
            return 0;
        }

        uint[] words = BytesToWords(spirv);
        if (words.Length < SpvOpCode.HeaderWordCount || words[0] != SpvOpCode.MagicNumber)
        {
            return 0;
        }

        Dictionary<uint, uint> loadToVar = new();
        Dictionary<uint, int?> varToSet = new();
        Dictionary<uint, int?> varToBinding = new();
        List<(uint ImageLoadId, uint SamplerLoadId)> sampledImagePairs = new();

        int offset = SpvOpCode.HeaderWordCount;
        while (offset < words.Length)
        {
            uint header = words[offset];
            ushort opCode = SpvOpCode.GetOpCode(header);
            ushort wordCount = SpvOpCode.GetWordCount(header);
            if (wordCount == 0)
            {
                break;
            }

            switch (opCode)
            {
                case SpvOpCode.OpDecorate when wordCount >= 4:
                    {
                        uint targetId = words[offset + 1];
                        uint decoration = words[offset + 2];
                        if (decoration == SpvOpCode.DecorationDescriptorSet)
                        {
                            varToSet[targetId] = (int)words[offset + 3];
                        }
                        else if (decoration == SpvOpCode.DecorationBinding)
                        {
                            varToBinding[targetId] = (int)words[offset + 3];
                        }
                        break;
                    }
                case OpLoad when wordCount >= 4:
                    {
                        uint resultId = words[offset + 2];
                        uint pointerId = words[offset + 3];
                        loadToVar[resultId] = pointerId;
                        break;
                    }
                case OpSampledImage when wordCount >= 5:
                    {
                        uint imageOperand = words[offset + 3];
                        uint samplerOperand = words[offset + 4];
                        sampledImagePairs.Add((imageOperand, samplerOperand));
                        break;
                    }
            }

            offset += wordCount;
        }

        if (sampledImagePairs.Count == 0)
        {
            return 0;
        }

        Dictionary<int, string> samplerNameByBinding = new();
        foreach (SamplerParameter sampler in symbols.SamplerParameters)
        {
            if (symbols.GetSetIdFor(sampler.BindPoint, ShaderResourceType.Sampler) != 0 || string.IsNullOrWhiteSpace(sampler.Name))
            {
                continue;
            }
            samplerNameByBinding[sampler.BindPoint] = sampler.Name!;
        }
        if (samplerNameByBinding.Count == 0)
        {
            return 0;
        }

        HashSet<int> existingTextureBindings = new();
        foreach (TextureParameter texture in symbols.TextureParameters)
        {
            if (symbols.GetSetIdFor(texture.Index, ShaderResourceType.Texture) == 0)
            {
                existingTextureBindings.Add(texture.Index);
            }
        }

        Dictionary<int, HashSet<int>> texturesPerSamplerBinding = new();
        Dictionary<(int, int), bool> resolvedPairs = new();
        foreach ((uint imageLoadId, uint samplerLoadId) in sampledImagePairs)
        {
            if (!loadToVar.TryGetValue(imageLoadId, out uint imageVarId)
                || !loadToVar.TryGetValue(samplerLoadId, out uint samplerVarId))
            {
                continue;
            }

            int? imageSet = varToSet.GetValueOrDefault(imageVarId);
            int? imageBinding = varToBinding.GetValueOrDefault(imageVarId);
            int? samplerSet = varToSet.GetValueOrDefault(samplerVarId);
            int? samplerBinding = varToBinding.GetValueOrDefault(samplerVarId);
            if (imageSet != 0 || imageBinding == null || samplerSet != 0 || samplerBinding == null)
            {
                continue;
            }

            int sb = samplerBinding.Value;
            int ib = imageBinding.Value;
            if (!texturesPerSamplerBinding.TryGetValue(sb, out HashSet<int>? texSet))
            {
                texSet = new HashSet<int>();
                texturesPerSamplerBinding[sb] = texSet;
            }
            texSet.Add(ib);
            resolvedPairs[(sb, ib)] = true;
        }

        int appended = 0;
        HashSet<int> alreadyInferred = new();
        foreach (var kvp in resolvedPairs)
        {
            int samplerBinding = kvp.Key.Item1;
            int imageBinding = kvp.Key.Item2;

            if (texturesPerSamplerBinding[samplerBinding].Count != 1)
            {
                continue;
            }
            if (existingTextureBindings.Contains(imageBinding) || alreadyInferred.Contains(imageBinding))
            {
                continue;
            }
            if (!samplerNameByBinding.TryGetValue(samplerBinding, out string? samplerName))
            {
                continue;
            }

            string? textureName = DeriveTextureNameFromSamplerName(samplerName);
            if (textureName == null)
            {
                continue;
            }

            symbols.TextureParameters.Add(new TextureParameter
            {
                Name = textureName,
                NameIndex = -1,
                Index = imageBinding,
                SamplerIndex = samplerBinding,
                MultiSampled = false,
                Dim = 2,
            });
            alreadyInferred.Add(imageBinding);
            appended++;
        }

        // After the sampler-name inference fills in the Material_Texture2D_N
        // texture entries, gap-fill any missing N values between consecutive
        // SRT-resolved/inferred Material_Texture2D_N slots. UE's Material UB
        // resource list interleaves (texture, sampler, texture, sampler, …),
        // so consecutive USED textures take consecutive t-slots and their
        // resource indices differ by 2 — but the materialLayout names them
        // with 1-stride suffixes. When (slotDelta == suffixDelta > 1), the
        // gap slots get Material_Texture2D_<a_N + step>.
        appended += FillMaterialTextureGaps(symbols);
        return appended;
    }

    // Find consecutive Material_Texture2D_N entries in symbols.TextureParameters
    // where (slotDelta == suffixDelta > 1) and synthesise the missing
    // N values for the intermediate slots. Returns the number of entries
    // added. Pure metadata mutation — no SPIR-V awareness needed.
    private static int FillMaterialTextureGaps(SerializedProgramData symbols)
    {
        List<(int Slot, int N)> materials = new();
        foreach (TextureParameter t in symbols.TextureParameters)
        {
            if (string.IsNullOrEmpty(t.Name)) continue;
            if (!t.Name.StartsWith("Material_Texture2D_", StringComparison.Ordinal)) continue;
            string tail = t.Name.Substring("Material_Texture2D_".Length);
            if (int.TryParse(tail, out int n))
            {
                materials.Add((Slot: t.Index, N: n));
            }
        }
        if (materials.Count < 2) return 0;
        materials.Sort((a, b) => a.Slot.CompareTo(b.Slot));

        HashSet<int> claimedSlots = new();
        foreach (TextureParameter t in symbols.TextureParameters) claimedSlots.Add(t.Index);

        int added = 0;
        for (int i = 0; i + 1 < materials.Count; i++)
        {
            (int aSlot, int aN) = materials[i];
            (int bSlot, int bN) = materials[i + 1];
            int slotDelta = bSlot - aSlot;
            int nDelta = bN - aN;
            if (slotDelta <= 1 || nDelta <= 1 || slotDelta != nDelta) continue;

            for (int step = 1; step < slotDelta; step++)
            {
                int fillSlot = aSlot + step;
                int fillN = aN + step;
                if (claimedSlots.Contains(fillSlot)) continue;
                symbols.TextureParameters.Add(new TextureParameter
                {
                    Name = $"Material_Texture2D_{fillN}",
                    NameIndex = -1,
                    Index = fillSlot,
                    SamplerIndex = -1,
                    MultiSampled = false,
                    Dim = 2,
                });
                claimedSlots.Add(fillSlot);
                added++;
            }
        }
        return added;
    }

    private static string? DeriveTextureNameFromSamplerName(string samplerName)
    {
        const string SamplerSuffix = "Sampler";
        if (!samplerName.EndsWith(SamplerSuffix, StringComparison.Ordinal))
        {
            return null;
        }
        if (!samplerName.StartsWith("Material_", StringComparison.Ordinal))
        {
            return null;
        }

        string textureName = samplerName[..^SamplerSuffix.Length];
        if (textureName.EndsWith("_Wrap_WorldGroupSettings", StringComparison.Ordinal)
            || textureName.EndsWith("_Clamp_WorldGroupSettings", StringComparison.Ordinal))
        {
            return null;
        }

        return textureName;
    }

    private static uint[] BytesToWords(byte[] bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, words.Length * 4);
        return words;
    }
}
