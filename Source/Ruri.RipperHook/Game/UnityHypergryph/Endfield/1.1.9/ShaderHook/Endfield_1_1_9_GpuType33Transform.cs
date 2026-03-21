using AssetRipper.Primitives;
using AssetsTools.NET;
using K4os.Compression.LZ4;

namespace Ruri.RipperHook.Endfield;

/// <summary>
/// Transforms Endfield v1.1.9 custom GpuProgramType=33 (combined VS+PS DXBC in one blob entry)
/// into standard DX11VertexSM40 (15) and DX11PixelSM40 (17) separate entries
/// so USCSandbox can decompile them to HLSL.
/// </summary>
internal static class Endfield_1_1_9_GpuType33Transform
{
    public static bool IsEnabled { get; set; }

    private const sbyte GpuType_Custom33 = 33;
    private const sbyte GpuType_DX11VertexSM40 = 15;
    private const sbyte GpuType_DX11PixelSM40 = 17;
    private const int Platform_D3D11 = 4;
    private const int StandardBlobVersion = 202012090; // Unity >= 2021.2

    // Custom ProgramData header layout offsets
    private const int PD_Offset2 = 0x04; // offset to 2nd DXBC
    private const int PD_Size2 = 0x08;   // size of 2nd DXBC
    private const int PD_Offset1 = 0x0C; // offset to 1st DXBC (= header size)
    private const int PD_Size1 = 0x10;   // size of 1st DXBC
    private const uint DXBC_Magic = 0x43425844;

    public static AssetTypeValueField Apply(AssetTypeValueField shaderData, UnityVersion version)
    {
        if (!IsEnabled) return shaderData;

        if (!HasGpuType33(shaderData))
            return shaderData;

        Console.WriteLine("[GpuType33Transform] Detected custom GpuType=33 entries, transforming...");

        int d3d11Index = FindPlatformIndex(shaderData);
        if (d3d11Index < 0)
        {
            Console.WriteLine("[GpuType33Transform] No d3d11 platform found, skipping");
            return shaderData;
        }

        // Step 1: Transform blob — split GpuType=33 entries into VS+PS
        var (splitMap, reindexMap) = TransformBlob(shaderData, d3d11Index, version);

        // Step 2: Transform PlayerSubPrograms in progVertex and progFragment
        TransformPlayerSubPrograms(shaderData, splitMap, reindexMap);

        return shaderData;
    }

    #region Detection

    private static bool HasGpuType33(AssetTypeValueField shaderData)
    {
        foreach (var subShader in shaderData["m_ParsedForm"]["m_SubShaders.Array"])
        {
            foreach (var pass in subShader["m_Passes.Array"])
            {
                var pspOuter = pass["progVertex"]["m_PlayerSubPrograms"];
                if (pspOuter.IsDummy) continue;
                var outerArray = pspOuter["Array"];
                if (outerArray.Children.Count == 0) continue;
                var lastInner = outerArray.Children[^1]["Array"];
                foreach (var entry in lastInner)
                {
                    if (entry["m_GpuProgramType"].AsSByte == GpuType_Custom33)
                        return true;
                }
            }
        }
        return false;
    }

    private static int FindPlatformIndex(AssetTypeValueField shaderData)
    {
        var platforms = shaderData["platforms.Array"];
        for (int i = 0; i < platforms.Children.Count; i++)
        {
            if (platforms[i].AsInt == Platform_D3D11)
                return i;
        }
        return -1;
    }

    #endregion

    #region Blob Transform

    /// <summary>
    /// Decompresses the d3d11 blob, splits GpuType=33 entries, rebuilds and recompresses.
    /// Returns: splitMap (old index → new VS+PS indices), reindexMap (old index → new index for non-split entries).
    /// </summary>
    private static (Dictionary<int, (int vsIdx, int psIdx)> splitMap, Dictionary<int, int> reindexMap) TransformBlob(
        AssetTypeValueField shaderData, int d3d11Index, UnityVersion version)
    {
        // Read blob metadata from ATVF
        var offsets = shaderData["offsets.Array"];
        var compressedLengths = shaderData["compressedLengths.Array"];
        var decompressedLengths = shaderData["decompressedLengths.Array"];
        byte[] masterBlob = shaderData["compressedBlob.Array"].AsByteArray;

        uint blobOffset, compLen, decompLen;
        if (offsets[d3d11Index].Children.Count > 0)
        {
            blobOffset = offsets[d3d11Index]["Array"][0].AsUInt;
            compLen = compressedLengths[d3d11Index]["Array"][0].AsUInt;
            decompLen = decompressedLengths[d3d11Index]["Array"][0].AsUInt;
        }
        else
        {
            blobOffset = offsets[d3d11Index].AsUInt;
            compLen = compressedLengths[d3d11Index].AsUInt;
            decompLen = decompressedLengths[d3d11Index].AsUInt;
        }

        // Decompress
        byte[] decompressed = new byte[decompLen];
        LZ4Codec.Decode(masterBlob, (int)blobOffset, (int)compLen, decompressed, 0, (int)decompLen);

        // Parse blob entries (BlobManager format)
        bool hasSegment = version.GreaterThanOrEquals(2019, 3);
        int entryFieldSize = hasSegment ? 12 : 8;

        using var blobReader = new BinaryReader(new MemoryStream(decompressed));
        int entryCount = blobReader.ReadInt32();
        var blobEntries = new List<(int offset, int length, int segment)>();
        for (int i = 0; i < entryCount; i++)
        {
            int off = blobReader.ReadInt32();
            int len = blobReader.ReadInt32();
            int seg = hasSegment ? blobReader.ReadInt32() : 0;
            blobEntries.Add((off, len, seg));
        }

        // Split GpuType=33 entries and build old→new index mappings
        var newEntryData = new List<byte[]>();
        var splitMap = new Dictionary<int, (int vsIdx, int psIdx)>();
        var reindexMap = new Dictionary<int, int>(); // old index → new index for non-split entries

        for (int i = 0; i < entryCount; i++)
        {
            byte[] rawEntry = new byte[blobEntries[i].length];
            Array.Copy(decompressed, blobEntries[i].offset, rawEntry, 0, blobEntries[i].length);

            // Check ProgramType at offset 4 (after blobVersion)
            int progType = BitConverter.ToInt32(rawEntry, 4);

            if (progType == GpuType_Custom33)
            {
                var (vsEntry, psEntry) = SplitCustomEntry(rawEntry, version);
                int vsIdx = newEntryData.Count;
                newEntryData.Add(vsEntry);
                int psIdx = newEntryData.Count;
                newEntryData.Add(psEntry);
                splitMap[i] = (vsIdx, psIdx);
            }
            else
            {
                int newIdx = newEntryData.Count;
                reindexMap[i] = newIdx;
                newEntryData.Add(rawEntry);
            }
        }

        // Rebuild blob binary
        byte[] newDecompressed = RebuildBlobBinary(newEntryData, hasSegment);

        // Recompress
        byte[] newCompressed = new byte[LZ4Codec.MaximumOutputSize(newDecompressed.Length)];
        int newCompSize = LZ4Codec.Encode(newDecompressed, newCompressed, LZ4Level.L00_FAST);
        Array.Resize(ref newCompressed, newCompSize);

        // Rebuild master blob (replace d3d11 chunk)
        using var newMasterStream = new MemoryStream();
        // Before d3d11
        newMasterStream.Write(masterBlob, 0, (int)blobOffset);
        uint newD3d11Offset = (uint)newMasterStream.Position;
        // New d3d11 data
        newMasterStream.Write(newCompressed, 0, newCompressed.Length);
        // After d3d11
        uint oldEnd = blobOffset + compLen;
        if (oldEnd < masterBlob.Length)
            newMasterStream.Write(masterBlob, (int)oldEnd, masterBlob.Length - (int)oldEnd);
        byte[] newMasterBlob = newMasterStream.ToArray();

        int sizeShift = newCompressed.Length - (int)compLen;

        // Update ATVF blob data
        shaderData["compressedBlob"]["Array"].Value = new AssetTypeValue(AssetValueType.ByteArray, newMasterBlob);

        // Update offsets/lengths for d3d11 platform
        UpdateBlobMetadata(offsets, d3d11Index, newD3d11Offset);
        UpdateBlobMetadata(compressedLengths, d3d11Index, (uint)newCompressed.Length);
        UpdateBlobMetadata(decompressedLengths, d3d11Index, (uint)newDecompressed.Length);

        // Shift offsets for platforms after d3d11
        if (sizeShift != 0)
        {
            for (int i = 0; i < shaderData["platforms.Array"].Children.Count; i++)
            {
                if (i == d3d11Index) continue;
                uint curOffset;
                if (offsets[i].Children.Count > 0)
                    curOffset = offsets[i]["Array"][0].AsUInt;
                else
                    curOffset = offsets[i].AsUInt;

                if (curOffset > blobOffset)
                    UpdateBlobMetadata(offsets, i, (uint)(curOffset + sizeShift));
            }
        }

        Console.WriteLine($"[GpuType33Transform] Blob: {entryCount} entries → {newEntryData.Count} entries, " +
                          $"decompressed {decompLen}→{newDecompressed.Length}, compressed {compLen}→{newCompressed.Length}");

        return (splitMap, reindexMap);
    }

    private static void UpdateBlobMetadata(AssetTypeValueField arrayField, int platformIndex, uint newValue)
    {
        if (arrayField[platformIndex].Children.Count > 0)
            arrayField[platformIndex]["Array"][0].Value = new AssetTypeValue(AssetValueType.UInt32, newValue);
        else
            arrayField[platformIndex].Value = new AssetTypeValue(AssetValueType.UInt32, newValue);
    }

    /// <summary>
    /// Splits a GpuType=33 blob entry into separate VS (type 15) and PS (type 17) entries.
    /// </summary>
    private static (byte[] vsEntry, byte[] psEntry) SplitCustomEntry(byte[] entryData, UnityVersion version)
    {
        using var reader = new BinaryReader(new MemoryStream(entryData));

        // Read ShaderSubProgram header
        int blobVersion = reader.ReadInt32();    // custom blobVersion
        int programType = reader.ReadInt32();    // 33
        int statsALU = reader.ReadInt32();
        int statsTEX = reader.ReadInt32();
        int statsFlow = reader.ReadInt32();
        int statsTempReg = reader.ReadInt32();   // present for Unity >= 5.5

        // Read keywords (single merged array for Unity >= 2021.2)
        // USCSandbox reads: globalKeywordCount + strings (no local keywords for 2021.3)
        int keywordCount = reader.ReadInt32();
        var keywordBytes = new List<byte[]>();
        for (int i = 0; i < keywordCount; i++)
        {
            int len = reader.ReadInt32();
            byte[] str = reader.ReadBytes(len);
            keywordBytes.Add(str);
            // Align to 4 bytes
            int pad = (4 - ((int)reader.BaseStream.Position % 4)) % 4;
            if (pad > 0) reader.ReadBytes(pad);
        }

        // Read ProgramData
        int programDataSize = reader.ReadInt32();
        byte[] programData = reader.ReadBytes(programDataSize);
        // Align to 4
        int pdPad = (4 - ((int)reader.BaseStream.Position % 4)) % 4;
        if (pdPad > 0) reader.ReadBytes(pdPad);

        // Read remaining bytes (BindChannels data)
        byte[] tail = reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position));

        // Parse custom ProgramData header to extract two DXBCs
        int offset1 = BitConverter.ToInt32(programData, PD_Offset1); // 1st DXBC offset (typically 0xB0)
        int size1 = BitConverter.ToInt32(programData, PD_Size1);     // 1st DXBC size (VS)
        int offset2 = BitConverter.ToInt32(programData, PD_Offset2); // 2nd DXBC offset
        int size2 = BitConverter.ToInt32(programData, PD_Size2);     // 2nd DXBC size (PS)

        byte[] vsDxbc = new byte[size1];
        Array.Copy(programData, offset1, vsDxbc, 0, size1);
        byte[] psDxbc = new byte[size2];
        Array.Copy(programData, offset2, psDxbc, 0, size2);

        // Build standard ProgramData: 6-byte header + raw DXBC
        // SegmentStream uses absolute offset from stream start.
        // GetDirectXDataOffset returns 6 for version>=5.4, headerVersion<2.
        // So DXBC starts at byte 6 (0-indexed): [headerVersion(1), zeros(5), DXBC...]
        byte[] vsProgData = new byte[6 + vsDxbc.Length];
        vsProgData[0] = 0x01; // headerVersion = 1
        Array.Copy(vsDxbc, 0, vsProgData, 6, vsDxbc.Length);

        byte[] psProgData = new byte[6 + psDxbc.Length];
        psProgData[0] = 0x01;
        Array.Copy(psDxbc, 0, psProgData, 6, psDxbc.Length);

        // Build complete ShaderSubProgram entries
        byte[] vsEntry = BuildSubProgramEntry(
            StandardBlobVersion, GpuType_DX11VertexSM40,
            statsALU, statsTEX, statsFlow, statsTempReg,
            keywordBytes, vsProgData, tail);

        byte[] psEntry = BuildSubProgramEntry(
            StandardBlobVersion, GpuType_DX11PixelSM40,
            statsALU, statsTEX, statsFlow, statsTempReg,
            keywordBytes, psProgData, tail);

        return (vsEntry, psEntry);
    }

    private static byte[] BuildSubProgramEntry(
        int blobVersion, int programType,
        int statsALU, int statsTEX, int statsFlow, int statsTempReg,
        List<byte[]> keywordBytes, byte[] programData, byte[] tail)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(blobVersion);
        writer.Write(programType);
        writer.Write(statsALU);
        writer.Write(statsTEX);
        writer.Write(statsFlow);
        writer.Write(statsTempReg);

        // Keywords
        writer.Write(keywordBytes.Count);
        foreach (var kw in keywordBytes)
        {
            writer.Write(kw.Length);
            writer.Write(kw);
            // Align to 4
            int pad = (4 - ((int)ms.Position % 4)) % 4;
            for (int i = 0; i < pad; i++) writer.Write((byte)0);
        }

        // ProgramData
        writer.Write(programData.Length);
        writer.Write(programData);
        // Align to 4
        int pdPad = (4 - ((int)ms.Position % 4)) % 4;
        for (int i = 0; i < pdPad; i++) writer.Write((byte)0);

        // BindChannels + trailing data
        writer.Write(tail);

        return ms.ToArray();
    }

    private static byte[] RebuildBlobBinary(List<byte[]> entries, bool hasSegment)
    {
        int entryFieldSize = hasSegment ? 12 : 8;
        int headerSize = 4 + entries.Count * entryFieldSize;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Reserve header space
        writer.BaseStream.Position = headerSize;

        var entryOffsets = new int[entries.Count];
        var entryLengths = new int[entries.Count];

        for (int i = 0; i < entries.Count; i++)
        {
            entryOffsets[i] = (int)writer.BaseStream.Position;
            entryLengths[i] = entries[i].Length;
            writer.Write(entries[i]);
        }

        // Write header
        writer.BaseStream.Position = 0;
        writer.Write(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            writer.Write(entryOffsets[i]);
            writer.Write(entryLengths[i]);
            if (hasSegment) writer.Write(0); // segment = 0
        }

        return ms.ToArray();
    }

    #endregion

    #region PlayerSubPrograms Transform

    /// <summary>
    /// Updates progVertex PlayerSubPrograms (GpuType 33→15, new BlobIndex)
    /// and builds progFragment PlayerSubPrograms (GpuType=17, PS BlobIndex).
    /// Also reindexes ParameterBlobIndices to account for entry index shifts.
    /// </summary>
    private static void TransformPlayerSubPrograms(
        AssetTypeValueField shaderData,
        Dictionary<int, (int vsIdx, int psIdx)> splitMap,
        Dictionary<int, int> reindexMap)
    {
        foreach (var subShader in shaderData["m_ParsedForm"]["m_SubShaders.Array"])
        {
            foreach (var pass in subShader["m_Passes.Array"])
            {
                var progVertex = pass["progVertex"];
                var progFragment = pass["progFragment"];

                if (progVertex.IsDummy) continue;

                var vtxPsp = progVertex["m_PlayerSubPrograms"];
                if (vtxPsp.IsDummy) continue;
                var vtxOuter = vtxPsp["Array"];
                if (vtxOuter.Children.Count == 0) continue;

                var vtxLastVector = vtxOuter.Children[^1];
                var vtxLastArray = vtxLastVector["Array"];
                if (vtxLastArray.Children.Count == 0) continue;

                // Build PS PlayerSubProgram entries, update VS entries, and reindex all BlobIndices
                var psEntries = new List<AssetTypeValueField>();

                foreach (var entry in vtxLastArray)
                {
                    uint oldBlobIndex = entry["m_BlobIndex"].AsUInt;

                    if (entry["m_GpuProgramType"].AsSByte == GpuType_Custom33)
                    {
                        if (!splitMap.TryGetValue((int)oldBlobIndex, out var mapping)) continue;

                        // Update VS: change GpuType to 15, BlobIndex to new VS index
                        entry["m_GpuProgramType"].Value = new AssetTypeValue(AssetValueType.Int8, GpuType_DX11VertexSM40);
                        entry["m_BlobIndex"].Value = new AssetTypeValue(AssetValueType.UInt32, (uint)mapping.vsIdx);

                        // Create PS entry (clone structure with GpuType=17 and PS BlobIndex)
                        var psEntry = ClonePlayerSubProgramEntry(entry, GpuType_DX11PixelSM40, (uint)mapping.psIdx);
                        psEntries.Add(psEntry);
                    }
                    else if (reindexMap.TryGetValue((int)oldBlobIndex, out int newIdx))
                    {
                        // Non-GpuType=33 entries (e.g. SPIRV) also need BlobIndex updated
                        entry["m_BlobIndex"].Value = new AssetTypeValue(AssetValueType.UInt32, (uint)newIdx);
                    }
                }

                // Reindex ParameterBlobIndices in progVertex (they reference param blob entries whose indices shifted)
                var vtxParamIndices = progVertex["m_ParameterBlobIndices"];
                if (!vtxParamIndices.IsDummy)
                    ReindexParameterBlobIndices(vtxParamIndices, reindexMap);

                // Build progFragment's m_PlayerSubPrograms (only if we created PS entries)
                if (psEntries.Count > 0)
                {
                    var fragPspNode = BuildPlayerSubProgramsVector(vtxOuter.Children.Count, psEntries);
                    ReplaceChild(progFragment, "m_PlayerSubPrograms", fragPspNode);
                }

                // Build progFragment's m_ParameterBlobIndices (copy reindexed values from progVertex)
                if (psEntries.Count > 0 && !vtxParamIndices.IsDummy)
                {
                    var fragParamNode = CloneParameterBlobIndices(vtxParamIndices);
                    ReplaceChild(progFragment, "m_ParameterBlobIndices", fragParamNode);
                }
            }
        }
    }

    /// <summary>
    /// Updates ParameterBlobIndices in-place using the reindex map.
    /// </summary>
    private static void ReindexParameterBlobIndices(AssetTypeValueField paramIndicesNode, Dictionary<int, int> reindexMap)
    {
        var outerArray = paramIndicesNode["Array"];
        foreach (var innerVec in outerArray)
        {
            var innerArray = innerVec["Array"];
            foreach (var val in innerArray)
            {
                uint oldIdx = val.AsUInt;
                if (reindexMap.TryGetValue((int)oldIdx, out int newIdx))
                {
                    val.Value = new AssetTypeValue(AssetValueType.UInt32, (uint)newIdx);
                }
            }
        }
    }

    private static AssetTypeValueField ClonePlayerSubProgramEntry(
        AssetTypeValueField source, sbyte newGpuType, uint newBlobIndex)
    {
        // Clone KeywordIndices
        var kwSrc = source["m_KeywordIndices.Array"];
        var kwChildren = new List<AssetTypeValueField>();
        foreach (var kw in kwSrc)
        {
            kwChildren.Add(new AssetTypeValueField
            {
                TemplateField = kw.TemplateField,
                Value = new AssetTypeValue(AssetValueType.UInt16, kw.AsUShort),
                Children = new List<AssetTypeValueField>()
            });
        }

        var kwSizeTemplate = new AssetTypeTemplateField { Name = "size", Type = "int", ValueType = AssetValueType.Int32, HasValue = true };
        var kwDataTemplate = new AssetTypeTemplateField { Name = "data", Type = "unsigned short", ValueType = AssetValueType.UInt16, HasValue = true, IsAligned = true };
        var kwArrayTemplate = new AssetTypeTemplateField
        {
            Name = "Array", Type = "Array", IsArray = true, IsAligned = true,
            ValueType = AssetValueType.Array,
            Children = new List<AssetTypeTemplateField> { kwSizeTemplate, kwDataTemplate }
        };
        var kwVectorTemplate = new AssetTypeTemplateField
        {
            Name = "m_KeywordIndices", Type = "vector", ValueType = AssetValueType.None,
            Children = new List<AssetTypeTemplateField> { kwArrayTemplate }
        };

        var kwArrayField = new AssetTypeValueField
        {
            TemplateField = kwArrayTemplate,
            Value = new AssetTypeValue(AssetValueType.Array, new AssetTypeArrayInfo { size = kwChildren.Count }),
            Children = kwChildren
        };
        var kwVectorField = new AssetTypeValueField
        {
            TemplateField = kwVectorTemplate,
            Value = null,
            Children = new List<AssetTypeValueField> { kwArrayField }
        };

        // Build the PlayerSubProgram node
        var blobIdxField = new AssetTypeValueField
        {
            TemplateField = new AssetTypeTemplateField { Name = "m_BlobIndex", Type = "unsigned int", ValueType = AssetValueType.UInt32, HasValue = true, Children = new List<AssetTypeTemplateField>() },
            Value = new AssetTypeValue(AssetValueType.UInt32, newBlobIndex),
            Children = new List<AssetTypeValueField>()
        };
        var gpuTypeField = new AssetTypeValueField
        {
            TemplateField = new AssetTypeTemplateField { Name = "m_GpuProgramType", Type = "SInt8", ValueType = AssetValueType.Int8, HasValue = true, IsAligned = true, Children = new List<AssetTypeTemplateField>() },
            Value = new AssetTypeValue(AssetValueType.Int8, newGpuType),
            Children = new List<AssetTypeValueField>()
        };

        var children = new List<AssetTypeValueField> { blobIdxField, kwVectorField, gpuTypeField };
        var template = new AssetTypeTemplateField
        {
            Name = "data", Type = "SerializedPlayerSubProgram", ValueType = AssetValueType.None,
            Children = children.Select(c => c.TemplateField).ToList()
        };

        return new AssetTypeValueField { TemplateField = template, Children = children };
    }

    /// <summary>
    /// Builds a m_PlayerSubPrograms vector with N-1 empty inner vectors + 1 with data.
    /// Mirrors the structure from progVertex.
    /// </summary>
    private static AssetTypeValueField BuildPlayerSubProgramsVector(int outerCount, List<AssetTypeValueField> dataEntries)
    {
        // Get the data template from the first entry
        var dataTemplate = dataEntries[0].TemplateField;

        // Build inner vectors: first (outerCount-1) are empty, last has data
        var outerChildren = new List<AssetTypeValueField>();
        for (int i = 0; i < outerCount; i++)
        {
            var innerChildren = (i == outerCount - 1) ? dataEntries : new List<AssetTypeValueField>();

            var innerSizeTemplate = new AssetTypeTemplateField { Name = "size", Type = "int", ValueType = AssetValueType.Int32, HasValue = true };
            var innerArrayTemplate = new AssetTypeTemplateField
            {
                Name = "Array", Type = "Array", IsArray = true, IsAligned = true,
                ValueType = AssetValueType.Array,
                Children = new List<AssetTypeTemplateField> { innerSizeTemplate, dataTemplate }
            };
            var innerVectorTemplate = new AssetTypeTemplateField
            {
                Name = "data", Type = "vector", ValueType = AssetValueType.None,
                Children = new List<AssetTypeTemplateField> { innerArrayTemplate }
            };

            var innerArrayField = new AssetTypeValueField
            {
                TemplateField = innerArrayTemplate,
                Value = new AssetTypeValue(AssetValueType.Array, new AssetTypeArrayInfo { size = innerChildren.Count }),
                Children = innerChildren
            };
            outerChildren.Add(new AssetTypeValueField
            {
                TemplateField = innerVectorTemplate,
                Value = null,
                Children = new List<AssetTypeValueField> { innerArrayField }
            });
        }

        // Build outer vector
        var outerInnerTemplate = outerChildren[0].TemplateField;
        var outerSizeTemplate = new AssetTypeTemplateField { Name = "size", Type = "int", ValueType = AssetValueType.Int32, HasValue = true };
        var outerArrayTemplate = new AssetTypeTemplateField
        {
            Name = "Array", Type = "Array", IsArray = true, IsAligned = true,
            ValueType = AssetValueType.Array,
            Children = new List<AssetTypeTemplateField> { outerSizeTemplate, outerInnerTemplate }
        };
        var outerVectorTemplate = new AssetTypeTemplateField
        {
            Name = "m_PlayerSubPrograms", Type = "vector", ValueType = AssetValueType.None,
            Children = new List<AssetTypeTemplateField> { outerArrayTemplate }
        };

        var outerArrayField = new AssetTypeValueField
        {
            TemplateField = outerArrayTemplate,
            Value = new AssetTypeValue(AssetValueType.Array, new AssetTypeArrayInfo { size = outerChildren.Count }),
            Children = outerChildren
        };
        return new AssetTypeValueField
        {
            TemplateField = outerVectorTemplate,
            Value = null,
            Children = new List<AssetTypeValueField> { outerArrayField }
        };
    }

    private static AssetTypeValueField CloneParameterBlobIndices(AssetTypeValueField source)
    {
        // Deep clone the m_ParameterBlobIndices structure
        var outerArray = source["Array"];
        var outerChildren = new List<AssetTypeValueField>();

        foreach (var innerVec in outerArray)
        {
            var innerArray = innerVec["Array"];
            var innerChildren = new List<AssetTypeValueField>();
            foreach (var val in innerArray)
            {
                innerChildren.Add(new AssetTypeValueField
                {
                    TemplateField = val.TemplateField,
                    Value = new AssetTypeValue(AssetValueType.UInt32, val.AsUInt),
                    Children = new List<AssetTypeValueField>()
                });
            }

            var clonedInnerArrayTemplate = innerArray.TemplateField;
            var clonedInnerArrayField = new AssetTypeValueField
            {
                TemplateField = clonedInnerArrayTemplate,
                Value = new AssetTypeValue(AssetValueType.Array, new AssetTypeArrayInfo { size = innerChildren.Count }),
                Children = innerChildren
            };
            outerChildren.Add(new AssetTypeValueField
            {
                TemplateField = innerVec.TemplateField,
                Value = null,
                Children = new List<AssetTypeValueField> { clonedInnerArrayField }
            });
        }

        var clonedOuterArrayTemplate = outerArray.TemplateField;
        var clonedOuterArrayField = new AssetTypeValueField
        {
            TemplateField = clonedOuterArrayTemplate,
            Value = new AssetTypeValue(AssetValueType.Array, new AssetTypeArrayInfo { size = outerChildren.Count }),
            Children = outerChildren
        };
        return new AssetTypeValueField
        {
            TemplateField = source.TemplateField,
            Value = null,
            Children = new List<AssetTypeValueField> { clonedOuterArrayField }
        };
    }

    private static void ReplaceChild(AssetTypeValueField parent, string childName, AssetTypeValueField newChild)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            if (parent.Children[i].TemplateField.Name == childName)
            {
                parent.Children[i] = newChild;
                // Also update the template's children list
                parent.TemplateField.Children[i] = newChild.TemplateField;
                return;
            }
        }
        // If not found, append
        parent.Children.Add(newChild);
        parent.TemplateField.Children.Add(newChild.TemplateField);
    }

    #endregion
}
