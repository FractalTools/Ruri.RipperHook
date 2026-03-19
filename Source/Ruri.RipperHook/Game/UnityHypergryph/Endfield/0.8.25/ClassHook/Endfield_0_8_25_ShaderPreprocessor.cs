using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgramParameters;
using K4os.Compression.LZ4;

namespace Ruri.RipperHook.Endfield;

/// <summary>
/// AKEF shader preprocessor: normalizes packed DXBC blobs and non-standard
/// GpuProgramType values so USCSandbox can process them without modification.
/// Called after TryResolveAndFillBlob() fills CompressedBlob.
/// </summary>
public static class AkefShaderPreprocessor
{
    // ShaderGpuProgramType enum values (Unity 5.5+)
    private const int DX11VertexSM40 = 15;
    private const int DX11PixelSM40 = 17;

    // Values above this are non-standard (AKEF custom)
    private const int MaxStandardGpuProgramType = 32;

    public static void PreprocessForDecompile(IShader shader)
    {
        try
        {
            // Step 1: Split packed DXBC blobs → separate vertex/pixel entries
            var pixelBlobIndices = SplitPackedDxbcBlobs(shader);

            // Step 2: Fix GpuProgramType + add fragment sub-programs
            FixSubPrograms(shader, pixelBlobIndices);

            // Step 3: Copy vertex CommonParameters to fragment if empty
            FixCommonParameters(shader);

            // Step 4: Remap hash-based BindIndices to sequential 0,1,2...
            RemapBindIndices(shader);

            if (pixelBlobIndices.Count > 0)
                HookLogger.LogSuccess($"    [ShaderPreprocessor] Done: split {pixelBlobIndices.Count} packed blob(s)");
        }
        catch (Exception ex)
        {
            HookLogger.LogWarning($"    [ShaderPreprocessor] Failed: {ex.Message}\n{ex.StackTrace}");
        }
    }

    #region Step 1: Split Packed DXBC Blobs

    /// <summary>
    /// Decompresses each platform segment, finds entries with multiple DXBCs packed together
    /// (vertex + pixel), creates separate entries for pixel DXBCs, recompresses.
    /// Returns mapping: original blob entry index → new pixel entry index.
    /// </summary>
    private static Dictionary<int, int> SplitPackedDxbcBlobs(IShader shader)
    {
        var result = new Dictionary<int, int>();

        if (shader.CompressedBlob == null || shader.CompressedBlob.Length == 0)
            return result;

        var offsets = shader.Offsets_AssetList_AssetList_UInt32;
        var compLengths = shader.CompressedLengths_AssetList_AssetList_UInt32;
        var decompLengths = shader.DecompressedLengths_AssetList_AssetList_UInt32;

        if (offsets == null || offsets.Count == 0)
            return result;

        byte[] compressedBlob = shader.CompressedBlob;

        using var masterStream = new MemoryStream();
        var newOffsetsList = new List<List<uint>>();
        var newCompLensList = new List<List<uint>>();
        var newDecompLensList = new List<List<uint>>();

        for (int platIdx = 0; platIdx < offsets.Count; platIdx++)
        {
            if (offsets[platIdx].Count == 0)
            {
                newOffsetsList.Add(new List<uint>());
                newCompLensList.Add(new List<uint>());
                newDecompLensList.Add(new List<uint>());
                continue;
            }

            uint segOffset = offsets[platIdx][0];
            uint segCompLen = compLengths[platIdx][0];
            uint segDecompLen = decompLengths[platIdx][0];

            // Decompress
            byte[] decompressed = new byte[segDecompLen];
            if (segCompLen == segDecompLen)
            {
                Buffer.BlockCopy(compressedBlob, (int)segOffset, decompressed, 0, (int)segCompLen);
            }
            else
            {
                int decoded = LZ4Codec.Decode(compressedBlob, (int)segOffset, (int)segCompLen, decompressed, 0, (int)segDecompLen);
                if (decoded <= 0)
                {
                    // Failed — keep original segment
                    uint newOff = (uint)masterStream.Position;
                    masterStream.Write(compressedBlob, (int)segOffset, (int)segCompLen);
                    newOffsetsList.Add(new List<uint> { newOff });
                    newCompLensList.Add(new List<uint> { segCompLen });
                    newDecompLensList.Add(new List<uint> { segDecompLen });
                    continue;
                }
            }

            // Parse entry table (3-field format: offset, length, segment — Unity 2021.3)
            int entryCount = BinaryPrimitives.ReadInt32LittleEndian(decompressed.AsSpan(0));
            const int ENTRY_SIZE = 12;
            int headerSize = 4 + entryCount * ENTRY_SIZE;

            // Collect new pixel entries to append
            var newEntryDataList = new List<byte[]>();

            for (int i = 0; i < entryCount; i++)
            {
                int eBase = 4 + i * ENTRY_SIZE;
                int eOffset = BinaryPrimitives.ReadInt32LittleEndian(decompressed.AsSpan(eBase));
                int eLength = BinaryPrimitives.ReadInt32LittleEndian(decompressed.AsSpan(eBase + 4));
                // int eSegment = BinaryPrimitives.ReadInt32LittleEndian(decompressed.AsSpan(eBase + 8));

                if (eOffset < headerSize || eOffset + eLength > decompressed.Length)
                    continue;

                // Extract entry bytes
                byte[] entryData = new byte[eLength];
                Buffer.BlockCopy(decompressed, eOffset, entryData, 0, eLength);

                byte[] pixelEntry = TrySplitPixelDxbc(entryData);
                if (pixelEntry != null)
                {
                    int newIdx = entryCount + newEntryDataList.Count;
                    newEntryDataList.Add(pixelEntry);
                    result[i] = newIdx;

                    // Also fix vertex ProgramType in original entry if non-standard
                    int origProgType = BinaryPrimitives.ReadInt32LittleEndian(decompressed.AsSpan(eOffset + 4));
                    if (origProgType > MaxStandardGpuProgramType)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(decompressed.AsSpan(eOffset + 4), DX11VertexSM40);
                    }
                }
            }

            if (newEntryDataList.Count == 0)
            {
                // No changes — recompress as-is
                byte[] recomp = CompressLZ4(decompressed);
                uint newOff = (uint)masterStream.Position;
                masterStream.Write(recomp, 0, recomp.Length);
                newOffsetsList.Add(new List<uint> { newOff });
                newCompLensList.Add(new List<uint> { (uint)recomp.Length });
                newDecompLensList.Add(new List<uint> { (uint)decompressed.Length });
                continue;
            }

            // Rebuild decompressed blob with new entries appended
            using var rebuildStream = new MemoryStream();
            int totalEntryCount = entryCount + newEntryDataList.Count;
            int newHeaderSize = 4 + totalEntryCount * ENTRY_SIZE;

            // Reserve header space
            rebuildStream.SetLength(newHeaderSize);
            rebuildStream.Position = newHeaderSize;

            // Copy original data (after original header)
            int origDataStart = headerSize;
            int origDataLen = decompressed.Length - origDataStart;
            int dataShift = newHeaderSize - headerSize; // how much data shifted due to larger header
            rebuildStream.Write(decompressed, origDataStart, origDataLen);

            // Append new pixel entries
            var newEntryOffsets = new List<int>();
            foreach (var pixelData in newEntryDataList)
            {
                newEntryOffsets.Add((int)rebuildStream.Position);
                rebuildStream.Write(pixelData, 0, pixelData.Length);
            }

            byte[] rebuilt = rebuildStream.ToArray();

            // Write entry table
            BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(0), totalEntryCount);

            // Original entries (with shifted offsets)
            for (int i = 0; i < entryCount; i++)
            {
                int eBase = 4 + i * ENTRY_SIZE;
                int origOff = BinaryPrimitives.ReadInt32LittleEndian(decompressed.AsSpan(eBase));
                int origLen = BinaryPrimitives.ReadInt32LittleEndian(decompressed.AsSpan(eBase + 4));
                int origSeg = BinaryPrimitives.ReadInt32LittleEndian(decompressed.AsSpan(eBase + 8));

                int newBase = 4 + i * ENTRY_SIZE;
                BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(newBase), origOff + dataShift);
                BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(newBase + 4), origLen);
                BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(newBase + 8), origSeg);
            }

            // New pixel entries
            for (int i = 0; i < newEntryDataList.Count; i++)
            {
                int newBase = 4 + (entryCount + i) * ENTRY_SIZE;
                BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(newBase), newEntryOffsets[i]);
                BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(newBase + 4), newEntryDataList[i].Length);
                BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(newBase + 8), 0); // segment 0
            }

            // Compress and append to master
            byte[] compSegment = CompressLZ4(rebuilt);
            uint masterOff = (uint)masterStream.Position;
            masterStream.Write(compSegment, 0, compSegment.Length);
            newOffsetsList.Add(new List<uint> { masterOff });
            newCompLensList.Add(new List<uint> { (uint)compSegment.Length });
            newDecompLensList.Add(new List<uint> { (uint)rebuilt.Length });
        }

        if (result.Count > 0)
        {
            // Update shader blob and offset arrays
            shader.CompressedBlob = masterStream.ToArray();

            shader.Offsets_AssetList_AssetList_UInt32.Clear();
            shader.CompressedLengths_AssetList_AssetList_UInt32.Clear();
            shader.DecompressedLengths_AssetList_AssetList_UInt32.Clear();

            for (int i = 0; i < newOffsetsList.Count; i++)
            {
                shader.Offsets_AssetList_AssetList_UInt32.AddNew().AddRange(newOffsetsList[i]);
                shader.CompressedLengths_AssetList_AssetList_UInt32.AddNew().AddRange(newCompLensList[i]);
                shader.DecompressedLengths_AssetList_AssetList_UInt32.AddNew().AddRange(newDecompLensList[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Given a single sub-program entry's raw bytes, check if ProgramData contains
    /// multiple packed DXBCs. If so, build and return a new entry containing only
    /// the pixel DXBC. Returns null if not packed or no pixel DXBC found.
    /// </summary>
    private static byte[] TrySplitPixelDxbc(byte[] entryData)
    {
        if (entryData.Length < 32)
            return null;

        // Parse sub-program header to locate ProgramData
        // Format (Unity 2021.3, blobVersion >= 202012090 → no local keywords):
        //   [version:4][programType:4][statsALU:4][statsTEX:4][statsFlow:4][statsTempReg:4]
        //   [global_keywords: string_array]
        //   [programDataLen:4][programData:N][align4]
        //   [tail: sourceMap + bindChannels]

        int pos = 24; // skip fixed header (6 × int32)

        // Skip global keywords: [count:int32] { [strlen:int32][chars:strlen][align4] }*
        pos = SkipStringArray(entryData, pos);
        if (pos < 0 || pos + 4 > entryData.Length)
            return null;

        // AKEF blobVersion (202103340) >= 202012090 → no local keywords in standard path.
        // But check for local keyword violation (case_a: extra int32 = 0 before real programDataSize).
        int programDataLen = BinaryPrimitives.ReadInt32LittleEndian(entryData.AsSpan(pos));
        pos += 4;

        // Heuristic: if programDataLen == 0, might be case_a local keyword violation
        // (an extra 0 count before the real ProgramData size)
        if (programDataLen == 0 && pos + 4 <= entryData.Length)
        {
            int nextVal = BinaryPrimitives.ReadInt32LittleEndian(entryData.AsSpan(pos));
            if (nextVal > 0 && nextVal <= entryData.Length - pos - 4)
            {
                programDataLen = nextVal;
                pos += 4;
            }
        }

        if (programDataLen <= 32 || pos + programDataLen > entryData.Length)
            return null;

        int progDataStart = pos;
        int progDataEnd = pos + programDataLen;

        // Find all DXBC magics in ProgramData
        var dxbcList = new List<(int offset, int size, string type)>();
        int searchPos = progDataStart;
        while (searchPos < progDataEnd - 4)
        {
            int found = FindDxbcMagic(entryData, searchPos, progDataEnd);
            if (found < 0) break;

            int dxbcSize = 0;
            if (found + 28 <= entryData.Length)
                dxbcSize = BinaryPrimitives.ReadInt32LittleEndian(entryData.AsSpan(found + 24));
            if (dxbcSize <= 0 || found + dxbcSize > progDataEnd)
                dxbcSize = progDataEnd - found;

            string type = DetectShaderTypeAt(entryData, found);
            dxbcList.Add((found, dxbcSize, type));
            searchPos = found + dxbcSize;
        }

        if (dxbcList.Count < 2)
            return null; // Not packed

        // Find pixel DXBC
        int pixelOffset = -1, pixelSize = -1;
        foreach (var (off, sz, t) in dxbcList)
        {
            if (t == "pixel")
            {
                pixelOffset = off;
                pixelSize = sz;
                break;
            }
        }

        if (pixelOffset < 0)
            return null;

        // Header before first DXBC in ProgramData (Unity sub-program header within ProgramData)
        int headerBeforeDxbc = dxbcList[0].offset - progDataStart;

        // Build new entry for pixel DXBC
        // Structure: [pre-ProgramData] + [new programDataLen:4] + [header + pixelDxbc] + [align4] + [tail]
        int preProgramData = progDataStart - 4; // bytes from entry start to (not including) programDataLen field
        //   actually: bytes from entry[0] through keywords, up to but not including programDataLen
        //   = pos - 4 relative to entry start... but pos already advanced past programDataLen.
        //   preProgramData = progDataStart - 4 - entryData_start... but entryData starts at 0.
        int prePDLen = progDataStart - 4; // number of bytes before the programDataLen field

        int newProgDataLen = headerBeforeDxbc + pixelSize;
        int alignedProgDataEnd = (progDataStart + programDataLen + 3) & ~3;
        int tailStart = alignedProgDataEnd;
        int tailLen = entryData.Length - tailStart;

        using var ms = new MemoryStream();

        // Copy everything before ProgramData length field
        ms.Write(entryData, 0, prePDLen);

        // Overwrite ProgramType at offset 4 with DX11PixelSM40
        long savedPos = ms.Position;
        ms.Position = 4;
        var ptBytes = BitConverter.GetBytes(DX11PixelSM40);
        ms.Write(ptBytes, 0, 4);
        ms.Position = savedPos;

        // Write new ProgramData length
        var lenBytes = BitConverter.GetBytes(newProgDataLen);
        ms.Write(lenBytes, 0, 4);

        // Write header-before-DXBC + pixel DXBC
        if (headerBeforeDxbc > 0)
            ms.Write(entryData, progDataStart, headerBeforeDxbc);
        ms.Write(entryData, pixelOffset, pixelSize);

        // Align to 4
        int written = newProgDataLen;
        int pad = ((written + 3) & ~3) - written;
        for (int p = 0; p < pad; p++) ms.WriteByte(0);

        // Copy tail (sourceMap + bindChannels)
        if (tailLen > 0)
            ms.Write(entryData, tailStart, tailLen);

        return ms.ToArray();
    }

    #endregion

    #region Step 2: Fix SubPrograms

    /// <summary>
    /// Fix GpuProgramType values > 32 to DX11VertexSM40, and add pixel sub-programs
    /// to ProgFragment using the new blob indices from Step 1.
    /// </summary>
    private static void FixSubPrograms(IShader shader, Dictionary<int, int> pixelBlobIndices)
    {
        var parsedForm = shader.ParsedForm;
        if (parsedForm == null) return;

        foreach (var subShader in parsedForm.SubShaders)
        {
            foreach (var pass in subShader.Passes)
            {
                FixPassSubPrograms(pass, pixelBlobIndices);
            }
        }
    }

    private static void FixPassSubPrograms(ISerializedPass pass, Dictionary<int, int> pixelBlobIndices)
    {
        var progVertex = pass.ProgVertex;
        var progFragment = pass.ProgFragment;
        if (progVertex?.PlayerSubPrograms == null) return;
        if (progFragment == null) return;

        // Fix vertex GpuProgramType
        foreach (var tierList in progVertex.PlayerSubPrograms)
        {
            foreach (var psp in tierList)
            {
                if (psp.GpuProgramType > MaxStandardGpuProgramType)
                {
                    SetProperty(psp, "GpuProgramType", (sbyte)DX11VertexSM40);
                }
            }
        }

        if (pixelBlobIndices.Count == 0) return;

        // Get mutable backing collections for fragment
        object fragPspOuter = GetBackingCollection(progFragment, "PlayerSubPrograms");
        object fragPbiOuter = GetBackingCollection(progFragment, "ParameterBlobIndices");

        if (fragPspOuter == null || fragPbiOuter == null)
        {
            HookLogger.LogWarning("    [ShaderPreprocessor] Cannot access fragment PlayerSubPrograms/ParameterBlobIndices");
            return;
        }

        for (int tierIdx = 0; tierIdx < progVertex.PlayerSubPrograms.Count; tierIdx++)
        {
            // Ensure fragment has enough tiers
            object fragPspTier = EnsureTierExists(fragPspOuter, tierIdx);
            object fragPbiTier = EnsureTierExists(fragPbiOuter, tierIdx);

            if (fragPspTier == null || fragPbiTier == null) continue;

            var vertexTier = progVertex.PlayerSubPrograms[tierIdx];

            for (int i = 0; i < vertexTier.Count; i++)
            {
                var vPsp = vertexTier[i];
                int vBlobIdx = (int)vPsp.BlobIndex;

                if (!pixelBlobIndices.TryGetValue(vBlobIdx, out int pBlobIdx))
                    continue;

                // Add new pixel PlayerSubProgram
                object newPsp = InvokeAddNew(fragPspTier);
                if (newPsp != null)
                {
                    SetProperty(newPsp, "BlobIndex", (uint)pBlobIdx);
                    SetProperty(newPsp, "GpuProgramType", (sbyte)DX11PixelSM40);
                    CopyReadOnlyListUShort(vPsp, newPsp, "KeywordIndices");
                }

                // Add matching ParameterBlobIndices entry
                // Use vertex's ParameterBlobIndices value for this sub-program
                if (progVertex.ParameterBlobIndices != null &&
                    tierIdx < progVertex.ParameterBlobIndices.Count &&
                    i < progVertex.ParameterBlobIndices[tierIdx].Count)
                {
                    uint paramBlobIdx = progVertex.ParameterBlobIndices[tierIdx][i];
                    InvokeAdd(fragPbiTier, paramBlobIdx);
                }
            }
        }
    }

    #endregion

    #region Step 3: Fix CommonParameters

    /// <summary>
    /// If ProgFragment.CommonParameters has no ConstantBuffers, copy them from ProgVertex.
    /// </summary>
    private static void FixCommonParameters(IShader shader)
    {
        var parsedForm = shader.ParsedForm;
        if (parsedForm == null) return;

        foreach (var subShader in parsedForm.SubShaders)
        {
            foreach (var pass in subShader.Passes)
            {
                var vertParams = pass.ProgVertex?.CommonParameters;
                var fragParams = pass.ProgFragment?.CommonParameters;

                if (vertParams == null || fragParams == null) continue;

                if (fragParams.ConstantBuffers.Count == 0 && vertParams.ConstantBuffers.Count > 0)
                {
                    CopyProgramParametersField(vertParams, fragParams, "ConstantBuffers");
                    CopyProgramParametersField(vertParams, fragParams, "ConstantBufferBindings");

                    HookLogger.LogRaw($"    [ShaderPreprocessor] Copied {vertParams.ConstantBuffers.Count} CB(s) to fragment");
                }
            }
        }
    }

    #endregion

    #region Step 4: Remap BindIndices

    /// <summary>
    /// Remap hash-based ConstantBufferBindings.Index to sequential 0,1,2...
    /// so USCSandbox's ConstBindings lookup (by register index) succeeds.
    /// </summary>
    private static void RemapBindIndices(IShader shader)
    {
        var parsedForm = shader.ParsedForm;
        if (parsedForm == null) return;

        foreach (var subShader in parsedForm.SubShaders)
        {
            foreach (var pass in subShader.Passes)
            {
                RemapProgramBindIndices(pass.ProgVertex);
                RemapProgramBindIndices(pass.ProgFragment);
            }
        }
    }

    private static void RemapProgramBindIndices(ISerializedProgram program)
    {
        if (program?.CommonParameters == null) return;

        var bindings = program.CommonParameters.ConstantBufferBindings;
        if (bindings == null || bindings.Count == 0) return;

        // Check if any binding has a hash-based Index (> small register range)
        bool needsRemap = false;
        foreach (var b in bindings)
        {
            if (b.Index > 64) // hash-based indices are very large numbers
            {
                needsRemap = true;
                break;
            }
        }

        if (!needsRemap) return;

        // Sequential remap: 0, 1, 2, ...
        for (int i = 0; i < bindings.Count; i++)
        {
            SetProperty(bindings[i], "Index", i);
        }

        HookLogger.LogRaw($"    [ShaderPreprocessor] Remapped {bindings.Count} CB binding index(es)");
    }

    #endregion

    #region DXBC Helpers (ported from DxbcDecompiler.cs)

    private static int FindDxbcMagic(byte[] data, int startOffset, int endOffset)
    {
        for (int i = startOffset; i <= endOffset - 4; i++)
        {
            if (data[i] == (byte)'D' && data[i + 1] == (byte)'X' &&
                data[i + 2] == (byte)'B' && data[i + 3] == (byte)'C')
                return i;
        }
        return -1;
    }

    private static string DetectShaderTypeAt(byte[] data, int dxbcStart)
    {
        if (dxbcStart + 32 > data.Length)
            return null;

        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(dxbcStart + 28));
        if (chunkCount <= 0 || chunkCount > 64)
            return null;

        int offsetTableStart = dxbcStart + 32;
        if (offsetTableStart + chunkCount * 4 > data.Length)
            return null;

        for (int c = 0; c < chunkCount; c++)
        {
            int chunkOffset = dxbcStart + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offsetTableStart + c * 4));
            if (chunkOffset + 12 > data.Length)
                continue;

            uint fourCC = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(chunkOffset));
            if (fourCC == 0x52444853 || fourCC == 0x58454853) // SHDR or SHEX
            {
                uint versionToken = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(chunkOffset + 8));
                int shaderType = (int)((versionToken >> 16) & 0xF);
                return shaderType switch
                {
                    0 => "pixel",
                    1 => "vertex",
                    2 => "geometry",
                    3 => "hull",
                    4 => "domain",
                    5 => "compute",
                    _ => null,
                };
            }
        }

        return null;
    }

    #endregion

    #region Binary Helpers

    private static int SkipStringArray(byte[] data, int pos)
    {
        if (pos + 4 > data.Length) return -1;
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
        pos += 4;
        if (count < 0 || count > 10000) return -1; // sanity check

        for (int i = 0; i < count; i++)
        {
            if (pos + 4 > data.Length) return -1;
            int strlen = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
            pos += 4;
            if (strlen < 0 || pos + strlen > data.Length) return -1;
            pos += strlen;
            pos = (pos + 3) & ~3; // align to 4
        }
        return pos;
    }

    private static byte[] CompressLZ4(byte[] data)
    {
        byte[] buffer = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
        int size = LZ4Codec.Encode(data, buffer, LZ4Level.L00_FAST);
        var result = new byte[size];
        Buffer.BlockCopy(buffer, 0, result, 0, size);
        return result;
    }

    #endregion

    #region Reflection Helpers

    private static void SetProperty(object obj, string name, object value)
    {
        if (obj == null) return;
        var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop == null) return;

        // Try direct setter first
        var setter = prop.GetSetMethod(true);
        if (setter != null)
        {
            setter.Invoke(obj, new[] { value });
            return;
        }

        // Try backing field
        var field = obj.GetType().GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null)
        {
            field.SetValue(obj, value);
        }
    }

    private static object GetBackingCollection(object obj, string propertyName)
    {
        if (obj == null) return null;
        var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return prop?.GetValue(obj);
    }

    private static object InvokeAddNew(object collection)
    {
        if (collection == null) return null;
        var method = collection.GetType().GetMethod("AddNew", BindingFlags.Public | BindingFlags.Instance);
        return method?.Invoke(collection, null);
    }

    private static void InvokeAdd(object collection, object item)
    {
        if (collection == null) return;
        var method = collection.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
        method?.Invoke(collection, new[] { item });
    }

    /// <summary>
    /// Ensure the outer collection has at least tierIdx+1 inner lists.
    /// Returns the inner list at tierIdx.
    /// </summary>
    private static object EnsureTierExists(object outerCollection, int tierIdx)
    {
        if (outerCollection == null) return null;

        var countProp = outerCollection.GetType().GetProperty("Count");
        int count = (int)(countProp?.GetValue(outerCollection) ?? 0);

        while (count <= tierIdx)
        {
            InvokeAddNew(outerCollection);
            count++;
        }

        // Access by indexer
        var indexer = outerCollection.GetType().GetProperty("Item");
        return indexer?.GetValue(outerCollection, new object[] { tierIdx });
    }

    private static void CopyReadOnlyListUShort(object source, object target, string propertyName)
    {
        var srcProp = source.GetType().GetProperty(propertyName);
        var tgtProp = target.GetType().GetProperty(propertyName);
        if (srcProp == null || tgtProp == null) return;

        var srcList = srcProp.GetValue(source);
        var tgtList = tgtProp.GetValue(target);
        if (srcList == null || tgtList == null) return;

        var addMethod = tgtList.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
        if (addMethod == null) return;

        var countProp = srcList.GetType().GetProperty("Count");
        int srcCount = (int)(countProp?.GetValue(srcList) ?? 0);

        var srcIndexer = srcList.GetType().GetProperty("Item");
        if (srcIndexer == null) return;

        for (int i = 0; i < srcCount; i++)
        {
            var val = srcIndexer.GetValue(srcList, new object[] { i });
            addMethod.Invoke(tgtList, new[] { val });
        }
    }

    /// <summary>
    /// Deep-copy a list field (e.g. ConstantBuffers, ConstantBufferBindings) from src to dst
    /// using AddNew() + CopyValues() pattern on AssetList elements.
    /// </summary>
    private static void CopyProgramParametersField(
        ISerializedProgramParameters src, ISerializedProgramParameters dst, string fieldName)
    {
        var srcProp = src.GetType().GetProperty(fieldName);
        var dstProp = dst.GetType().GetProperty(fieldName);
        if (srcProp == null || dstProp == null) return;

        var srcList = srcProp.GetValue(src);
        var dstList = dstProp.GetValue(dst);
        if (srcList == null || dstList == null) return;

        var srcCountProp = srcList.GetType().GetProperty("Count");
        int srcCount = (int)(srcCountProp?.GetValue(srcList) ?? 0);
        var srcIndexer = srcList.GetType().GetProperty("Item");
        if (srcIndexer == null || srcCount == 0) return;

        for (int i = 0; i < srcCount; i++)
        {
            var srcItem = srcIndexer.GetValue(srcList, new object[] { i });

            // AddNew creates a default instance of the element type
            var newItem = InvokeAddNew(dstList);
            if (newItem == null) continue;

            // Try CopyValues (AssetRipper convention)
            var copyMethod = newItem.GetType().GetMethod("CopyValues",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { srcItem.GetType() }, null);

            if (copyMethod != null)
            {
                copyMethod.Invoke(newItem, new[] { srcItem });
            }
            else
            {
                // Fallback: copy all writable properties individually
                CopyAllProperties(srcItem, newItem);
            }
        }
    }

    private static void CopyAllProperties(object src, object dst)
    {
        foreach (var prop in dst.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;
            var srcProp = src.GetType().GetProperty(prop.Name);
            if (srcProp == null || !srcProp.CanRead) continue;

            try
            {
                var val = srcProp.GetValue(src);
                prop.SetValue(dst, val);
            }
            catch
            {
                // Skip incompatible properties
            }
        }
    }

    #endregion
}
