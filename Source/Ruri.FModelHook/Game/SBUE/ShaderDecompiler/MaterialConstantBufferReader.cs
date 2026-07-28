using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

// EPreshaderOpcode layout changes between UE versions — see
// `Engine/Public/Shader/Preshader.h` per release:
//
//   * UE 5.0-5.3 (canonical layout used by this reader's case statements):
//       0..3 Nop/ConstantZero/Constant/Parameter
//       4..8 Add/Sub/Mul/Div/Fmod
//       9..11 Min/Max/Clamp
//       12..18 Sin/Cos/Tan/Asin/Acos/Atan/Atan2
//       19/20 Dot/Cross
//       21..35 Sqrt/Rcp/Length/Normalize/Saturate/Abs/Floor/Ceil/Round/Trunc/
//              Sign/Frac/Fractional/Log2/Log10
//       36/37 ComponentSwizzle/AppendVector
//       38..42 TextureSize/TexelSize/ExtTexCoordScaleRot/ExtTexCoordOffset/
//              RuntimeVirtualTextureUniform
//       43/44 GetField/SetField
//       45..53 Neg/Jump/JumpIfFalse/PushValue/Less/Assign/Greater/LessEqual/
//              GreaterEqual
//
//   * UE 5.4-5.6: inserts `SparseVolumeTextureUniform` at slot 43, pushing
//     GetField..GreaterEqual up by +1, and appends Exp/Exp2/Log at 55..57.
//
//   * UE 5.7+: inserts `Modulo` at slot 9, shifting EVERY opcode at
//     slot 9+ up by +1 (so Min=10, Max=11, ..., GreaterEqual=55, Exp=56,
//     Exp2=57, Log=58 in UE 5.7).
//
// The decoder's switch hardcodes the UE 5.1 numbering. For 5.4+ we
// translate the cooked byte to its UE 5.1 equivalent at decode time;
// opcodes that have no UE 5.1 counterpart (SparseVolumeTextureUniform,
// Modulo, Exp/Exp2/Log) translate to 255 — the default branch then
// safely aborts the preshader stream.
internal enum UeMaterialPreshaderVersion
{
    Ue51 = 51,  // UE 5.0-5.3 — canonical layout used by the switch
    Ue54 = 54,  // UE 5.4-5.6 — SparseVolumeTextureUniform inserted at 43
    Ue57 = 57,  // UE 5.7+ — Modulo inserted at 9 on top of the 5.4 shift
}

internal static class MaterialConstantBufferReader
{
    // Active preshader-opcode layout. Set once at pipeline startup
    // (DecompilePipeline.Run → after Pass140 has populated
    // `state.GameVersionEnum`). Default is Ue51 so existing 5.0-5.3
    // cooks keep working without any wiring change.
    public static UeMaterialPreshaderVersion PreshaderVersion { get; set; } = UeMaterialPreshaderVersion.Ue51;

    // Translate a cooked opcode byte to the UE 5.1 canonical numbering
    // the switch below expects. Returns 255 for opcodes with no 5.1
    // equivalent (Modulo, SparseVolumeTextureUniform, Exp/Exp2/Log) —
    // those land in the `default` arm and safely terminate decoding.
    //
    // Layout cheat sheet (numbers are the raw cooked opcode bytes that
    // map to each semantic op):
    //   UE 5.1     UE 5.4    UE 5.7    Semantic
    //   ---------  --------- --------- ----------------------------
    //   0-8        0-8       0-8        Nop..Fmod (identical)
    //   —          —         9          Modulo (no 5.1 equiv)
    //   9-42       9-42      10-43      Min..RuntimeVirtualTextureUniform
    //   —          43        44         SparseVolumeTextureUniform (no 5.1 equiv)
    //   43-53      44-54     45-55      GetField..GreaterEqual
    //   —          55/56/57  56/57/58   Exp/Exp2/Log (no 5.1 equiv)
    /// <summary>
    /// 每个材质的 <c>Material</c> cbuffer 成员**实算出来的值**:材质路径 → (成员名 → 分量文本)。
    ///
    /// 这是 <see cref="TryEvaluatePreshaderNumeric"/> 的产物,给导出侧(Pass200)写进容器头用。
    /// 键用**最终成员名**(已过 <c>_at_&lt;offset&gt;</c> 去重),因为消费侧在反编译出来的 HLSL 里
    /// 看到的就是这个名字。求不出值的成员**不出现在表里** —— 消费侧照旧回落到名字路径,
    /// 缺一条只是少一次修正,记一条错的会污染整个材质。
    /// </summary>
    public static readonly Dictionary<string, Dictionary<string, string>> EvaluatedCbufferValues = new(StringComparer.Ordinal);

    /// <summary>把一个成员的实算值记进 <see cref="EvaluatedCbufferValues"/>(按字段的真实分量数裁剪)。</summary>
    private static void RecordEvaluated(string materialPath, string memberName, int rows, float[]? value)
    {
        if (value == null || string.IsNullOrEmpty(materialPath)) return;
        if (!EvaluatedCbufferValues.TryGetValue(materialPath, out Dictionary<string, string>? byName))
        {
            byName = new Dictionary<string, string>(StringComparer.Ordinal);
            EvaluatedCbufferValues[materialPath] = byName;
        }
        int comps = Math.Clamp(rows, 1, 4);
        string[] parts = new string[comps];
        for (int c = 0; c < comps; c++)
        {
            parts[c] = value[c].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
        byName[memberName] = string.Join(",", parts);
    }

    private static byte TranslateOpcode(byte raw)
    {
        switch (PreshaderVersion)
        {
            case UeMaterialPreshaderVersion.Ue51:
                return raw;

            case UeMaterialPreshaderVersion.Ue54:
                if (raw <= 42) return raw;           // 0..42 unchanged
                if (raw == 43) return 255;           // SparseVolumeTextureUniform
                if (raw <= 54) return (byte)(raw - 1); // 44..54 → 43..53
                return 255;                          // 55+ = Exp/Exp2/Log etc.

            case UeMaterialPreshaderVersion.Ue57:
                if (raw <= 8) return raw;            // 0..8 unchanged
                if (raw == 9) return 255;            // Modulo
                if (raw <= 43) return (byte)(raw - 1); // 10..43 → 9..42 (5.1 layout)
                if (raw == 44) return 255;           // SparseVolumeTextureUniform
                if (raw <= 55) return (byte)(raw - 2); // 45..55 → 43..53
                return 255;                          // 56+ = Exp/Exp2/Log etc.
        }
        return raw;
    }

    // [preshader-debug] one-shot toggle: enabled only while the env var
    // RURI_PRESHADER_DEBUG is set. Filters via name substring to avoid spam.
    private static readonly string? PreshaderDebugFilter =
        Environment.GetEnvironmentVariable("RURI_PRESHADER_DEBUG");

    public static ConstantBufferParameter? Read(JsonElement uniformExpressionSet, string? materialPath = null)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformBufferLayoutInitializer", out JsonElement uniformBufferLayoutInitializer)
            || uniformBufferLayoutInitializer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? bufferName = ReadString(uniformBufferLayoutInitializer, "Name");
        if (!string.Equals(bufferName, "Material", StringComparison.Ordinal))
        {
            return null;
        }

        uint constantBufferSize = ReadUInt32(uniformBufferLayoutInitializer, "ConstantBufferSize");
        if (!uniformExpressionSet.TryGetProperty("UniformPreshaders", out JsonElement uniformPreshaders)
            || uniformPreshaders.ValueKind != JsonValueKind.Array
            || !uniformExpressionSet.TryGetProperty("UniformPreshaderFields", out JsonElement uniformPreshaderFields)
            || uniformPreshaderFields.ValueKind != JsonValueKind.Array
            || !uniformExpressionSet.TryGetProperty("UniformNumericParameters", out JsonElement uniformNumericParameters)
            || uniformNumericParameters.ValueKind != JsonValueKind.Array
            || !uniformExpressionSet.TryGetProperty("UniformPreshaderData", out JsonElement uniformPreshaderData)
            || uniformPreshaderData.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? encodedData = ReadString(uniformPreshaderData, "Data");
        if (string.IsNullOrWhiteSpace(encodedData))
        {
            return null;
        }

        byte[] opcodeData = Convert.FromBase64String(encodedData);
        ConstantBufferParameter materialBuffer = new()
        {
            Name = "Material",
            Size = checked((int)constantBufferSize)
        };

        // Preshader-data side tables. Opcodes 38-42 (Texture/Texel/RVT/External
        // texture coord) carry an FHashedMaterialParameterInfo whose name is
        // an index into UniformPreshaderData.Names, followed by an int32
        // TextureIndex that resolves into UniformTextureParameters[type][idx]
        // when the named parameter doesn't override. Extract both up-front so
        // the evaluator can produce real `<TextureName>_TextureSize`-style
        // names instead of falling back to anonymous `f_<offset>`.
        string[] preshaderNames = ExtractPreshaderNames(uniformPreshaderData);
        JsonElement uniformTextureParameters = default;
        uniformExpressionSet.TryGetProperty("UniformTextureParameters", out uniformTextureParameters);

        (int preshaderBufferStart, int vtPageTableBytes, int vtUniformBytes) = ComputeNumericLayout(uniformExpressionSet, (int)constantBufferSize);

        HashSet<int> seenOffsets = new();
        HashSet<string> seenNames = new(StringComparer.Ordinal);
        List<VectorParameter> vectorParams = new();
        List<MatrixParameter> matrixParams = new();

        if (vtPageTableBytes > 0)
        {
            vectorParams.Add(new VectorParameter
            {
                Name = "VTPackedPageTableUniform",
                NameIndex = -1,
                Type = ShaderParamType.UInt,
                Index = 0,
                ArraySize = vtPageTableBytes / 16,
                IsMatrix = false,
                RowCount = 4,
                ColumnCount = 1,
            });
            seenOffsets.Add(0);
            seenNames.Add("VTPackedPageTableUniform");
        }

        if (vtUniformBytes > 0)
        {
            int vtUniformStart = vtPageTableBytes;
            vectorParams.Add(new VectorParameter
            {
                Name = "VTPackedUniform",
                NameIndex = -1,
                Type = ShaderParamType.UInt,
                Index = vtUniformStart,
                ArraySize = vtUniformBytes / 16,
                IsMatrix = false,
                RowCount = 4,
                ColumnCount = 1,
            });
            seenOffsets.Add(vtUniformStart);
            seenNames.Add("VTPackedUniform");
        }

        foreach (JsonElement preshader in uniformPreshaders.EnumerateArray())
        {
            uint opcodeOffset = ReadUInt32(preshader, "OpcodeOffset");
            uint opcodeSize = ReadUInt32(preshader, "OpcodeSize");
            uint fieldIndex = ReadUInt32(preshader, "FieldIndex");
            uint numFields = ReadUInt32(preshader, "NumFields");
            // **多字段 preshader 也要收**。原来这里是 `numFields != 1 → continue`,整段跳过——
            // 于是它覆盖的那几个 cbuffer 偏移在成员表里根本不存在,反编译产物只能给它们编号占位
            // (`Material_Unmapped_at_<offset>`),消费侧永远解不出值、留 0。
            // 实测皮肤内核:`Unmapped_at_276` 是 `pow(x, k)` 的**指数**,留 0 让 `pow(x,0)=1`,
            // 直接选中"全湿"支路把整条基色顶掉 —— 一个被跳过的成员废掉一整条手臂。
            if (numFields < 1 || fieldIndex + numFields > (uint)uniformPreshaderFields.GetArrayLength())
            {
                continue;
            }

            // 一段 opcode 求一次值:多字段时栈底到栈顶依次对应 field 0..N-1。
            List<float[]>? stackValues = TryEvaluatePreshaderStack(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters);

            for (uint fieldSlot = 0; fieldSlot < numFields; fieldSlot++)
            {
            JsonElement field = uniformPreshaderFields[checked((int)(fieldIndex + fieldSlot))];
            FieldKind kind = TryMapFieldType(ReadString(field, "Type"), out int rows);
            if (kind == FieldKind.Unknown)
            {
                continue;
            }

            int byteOffset = preshaderBufferStart + checked((int)ReadUInt32(field, "BufferOffset") * 4);
            if (!seenOffsets.Add(byteOffset))
            {
                continue;
            }

            string baseName = DerivePreshaderName(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters, byteOffset, materialPath, rows, preshaderNames, uniformTextureParameters);
            // 多字段时一段 opcode 对应多个成员,名字会撞;按字段序号区分(去重层还会再补 `_at_<offset>`)。
            if (numFields > 1) baseName = $"{baseName}_f{fieldSlot}";

            // 名字之外**再算一份真值**。多字段时按"栈底到栈顶 = field 0..N-1"取对应那一项;
            // 单字段就是栈顶。算不出来就不记 —— 缺一条只是回落到名字路径,记一条错的会污染整个材质。
            float[]? evaluated = stackValues == null ? null
                : numFields == 1 ? stackValues[^1]
                : stackValues.Count == numFields ? stackValues[checked((int)fieldSlot)]
                : null;
            // 成员名只是「这段 preshader 的**主导参数**」的描述,不等于「这个槽 = 那个参数」。
            // 消费侧按名字查参数值时,凡是**派生**表达式(名字来自 TryRecoverViaSingleParamScan
            // 的单参数兜底)都会取错值——实测某皮肤材质的基色三元组,R/G/B 三个分量分别叫
            // WW_RainWetAmount / WW_WetNoiseSize / …,按名字取值出来是蓝的。
            // 要判定「这个名字是恒等还是派生」,唯一可靠的办法是看它的 opcode 流,
            // 所以调试开关打开时**逐成员**都 dump 一份,而不是只 dump 兜底成匿名的那些。
            DumpPreshaderDebug(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters, byteOffset, materialPath, rows, baseName);
            switch (kind)
            {
                case FieldKind.Float:
                case FieldKind.Numeric:
                {
                    string memberName = RegisterUniqueName(seenNames, baseName, byteOffset);
                    RecordEvaluated(materialPath, memberName, rows, evaluated);
                    AddVectorMember(vectorParams, memberName, byteOffset, rows, ShaderParamType.Float);
                    break;
                }
                case FieldKind.Int:
                {
                    string memberName = RegisterUniqueName(seenNames, baseName, byteOffset);
                    RecordEvaluated(materialPath, memberName, rows, evaluated);
                    AddVectorMember(vectorParams, memberName, byteOffset, rows, ShaderParamType.Int);
                    break;
                }
                case FieldKind.Bool:
                {
                    string memberName = RegisterUniqueName(seenNames, baseName, byteOffset);
                    RecordEvaluated(materialPath, memberName, rows, evaluated);
                    AddVectorMember(vectorParams, memberName, byteOffset, rows, ShaderParamType.Bool);
                    break;
                }
                case FieldKind.LwcDouble:
                    {
                        int totalComponents = rows * 2;
                        for (int c = 0; c < totalComponents; c++)
                        {
                            int compOffset = byteOffset + c * 4;
                            if (c > 0)
                            {
                                seenOffsets.Add(compOffset);
                            }
                            string compName = c < rows
                                ? $"{baseName}_LwcTile_{"xyzw"[c]}"
                                : $"{baseName}_LwcOffset_{"xyzw"[c - rows]}";
                            AddVectorMember(vectorParams, RegisterUniqueName(seenNames, compName, compOffset), compOffset, 1, ShaderParamType.Float);
                        }
                        break;
                    }
                case FieldKind.Float4x4:
                    AddMatrixMember(matrixParams, RegisterUniqueName(seenNames, baseName, byteOffset), byteOffset, ShaderParamType.Float);
                    break;
                case FieldKind.LwcDouble4x4:
                    {
                        int offsetPart = byteOffset + 64;
                        seenOffsets.Add(offsetPart);
                        AddMatrixMember(matrixParams, RegisterUniqueName(seenNames, $"{baseName}_LwcTile", byteOffset), byteOffset, ShaderParamType.Float);
                        AddMatrixMember(matrixParams, RegisterUniqueName(seenNames, $"{baseName}_LwcOffset", offsetPart), offsetPart, ShaderParamType.Float);
                        break;
                    }
            }
            }
        }

        if (vectorParams.Count == 0 && matrixParams.Count == 0)
        {
            return null;
        }

        materialBuffer.VectorParameters = vectorParams.OrderBy(static p => p.Index).ToArray();
        materialBuffer.MatrixParameters = matrixParams.OrderBy(static p => p.Index).ToArray();
        return materialBuffer;
    }

    private static (int preshaderBufferStart, int vtPageTableBytes, int vtUniformBytes) ComputeNumericLayout(JsonElement uniformExpressionSet, int constantBufferSize)
    {
        int preshaderBufferSizeFloat4 = 0;
        if (uniformExpressionSet.TryGetProperty("UniformPreshaderBufferSize", out JsonElement sizeElement) && sizeElement.ValueKind == JsonValueKind.Number)
        {
            preshaderBufferSizeFloat4 = sizeElement.GetInt32();
        }
        int preshaderBufferBytes = Math.Max(0, preshaderBufferSizeFloat4) * 16;

        int numericEnd = constantBufferSize;
        if (uniformExpressionSet.TryGetProperty("UniformBufferLayoutInitializer", out JsonElement ubl)
            && ubl.ValueKind == JsonValueKind.Object
            && ubl.TryGetProperty("Resources", out JsonElement resources)
            && resources.ValueKind == JsonValueKind.Array
            && resources.GetArrayLength() > 0
            && resources[0].TryGetProperty("MemberOffset", out JsonElement firstResourceOffset)
            && firstResourceOffset.ValueKind == JsonValueKind.Number)
        {
            numericEnd = firstResourceOffset.GetInt32();
        }

        int virtualCount = 0;
        if (uniformExpressionSet.TryGetProperty("UniformTextureParameters", out JsonElement textureParams)
            && textureParams.ValueKind == JsonValueKind.Array
            && textureParams.GetArrayLength() > 5
            && textureParams[5].ValueKind == JsonValueKind.Array)
        {
            virtualCount = textureParams[5].GetArrayLength();
        }
        int vtUniformBytes = virtualCount * 16;

        int vtPageTableBytes = numericEnd - preshaderBufferBytes - vtUniformBytes;
        if (vtPageTableBytes < 0)
        {
            vtPageTableBytes = 0;
        }

        int preshaderBufferStart = vtPageTableBytes + vtUniformBytes;
        return (preshaderBufferStart, vtPageTableBytes, vtUniformBytes);
    }

    private static string RegisterUniqueName(HashSet<string> seenNames, string candidate, int byteOffset)
    {
        // CRITICAL: sanitize FIRST, then dedupe. Two raw parameter names like
        // "AO " (trailing space) and "AO" both render to "AO_" in HLSL after
        // spirv-cross's identifier sanitisation. If we dedupe on the RAW
        // string the two entries pass the HashSet (different keys), but the
        // emitted HLSL has duplicate `Material_AO_` declarations and fails
        // to compile. Dedupe must operate on the post-sanitised form to
        // match the cbuffer member text that actually reaches the consumer.
        string sanitized = SanitizeHlslIdent(candidate);
        // An empty author name (or sanitisation collapsed it to "") would
        // emit as `Material_` — an illegal HLSL identifier (trailing _).
        // Substitute a byte-offset-based stable placeholder so the slot is
        // distinct and pronounceable.
        if (string.IsNullOrEmpty(sanitized)) sanitized = $"f_{byteOffset}";
        if (seenNames.Add(sanitized)) return sanitized;
        string disambiguated = $"{sanitized}_at_{byteOffset}";
        seenNames.Add(disambiguated);
        return disambiguated;
    }

    // Sanitize to a HLSL-safe identifier MATCHING spirv-cross's emit-side
    // rule: non-alphanumeric → `_`, collapse runs of `_`, trim trailing
    // `_`. CRITICAL for non-Latin author names (CJK, Cyrillic, etc.):
    // raw "AO对自发光的遮蔽强度" produces "AO_________" before collapse;
    // raw "AO强度" produces "AO__". Both collapse to "AO_" in HLSL,
    // colliding — and any dedup keyed on the un-collapsed form misses
    // this. By collapsing+trimming here, dedup sees the same key that
    // ends up in the shader source.
    private static string SanitizeHlslIdent(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new System.Text.StringBuilder(raw.Length);
        bool lastUnderscore = false;
        foreach (char c in raw)
        {
            bool isAlnum = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (isAlnum) { sb.Append(c); lastUnderscore = false; }
            else if (!lastUnderscore) { sb.Append('_'); lastUnderscore = true; }
        }
        // Trim leading AND trailing `_`. spirv-cross's HLSL emit also
        // collapses underscore runs across the cbuffer-variable prefix
        // boundary (`Material_` + `_AO` → `Material_AO`); trimming both
        // sides aligns dedup with the actual emitted form.
        int start = 0;
        while (start < sb.Length && sb[start] == '_') start++;
        int end = sb.Length;
        while (end > start && sb[end - 1] == '_') end--;
        if (end == start) return string.Empty;
        string body = sb.ToString(start, end - start);
        return (body[0] >= '0' && body[0] <= '9') ? "_" + body : body;
    }

    private static void AddVectorMember(List<VectorParameter> destination, string name, int byteOffset, int rows, ShaderParamType type)
    {
        destination.Add(new VectorParameter
        {
            Name = name,
            NameIndex = -1,
            Type = type,
            Index = byteOffset,
            ArraySize = 1,
            IsMatrix = false,
            RowCount = (byte)rows,
            ColumnCount = 1,
        });
    }

    private static void AddMatrixMember(List<MatrixParameter> destination, string name, int byteOffset, ShaderParamType type)
    {
        destination.Add(new MatrixParameter
        {
            Name = name,
            NameIndex = -1,
            Type = type,
            Index = byteOffset,
            ArraySize = 1,
            IsMatrix = true,
            RowCount = 4,
            ColumnCount = 4,
        });
    }

    private static string SwizzleSuffix(byte numE, byte r, byte g, byte b, byte a)
    {
        if (numE == 0 || numE > 4)
        {
            return string.Empty;
        }

        Span<byte> indices = stackalloc byte[4] { r, g, b, a };
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < numE; i++)
        {
            char c = indices[i] switch
            {
                0 => 'x',
                1 => 'y',
                2 => 'z',
                3 => 'w',
                _ => '\0',
            };
            if (c == '\0')
            {
                return string.Empty;
            }
            chars[i] = c;
        }
        return new string(chars[..numE]);
    }

    private static string DerivePreshaderName(byte[] data, uint offset, uint size, JsonElement parameters, int byteOffset, string? materialPath = null, int rows = 0, string[]? preshaderNames = null, JsonElement textureParameters = default)
    {
        string anonymous = $"f_{byteOffset}";
        if (size < 3 || offset >= (uint)data.Length || offset + 3 > (uint)data.Length)
        {
            return anonymous;
        }
        if (data[offset] != 3)
        {
            // Non-Parameter lead (typically a Constant pushed first, then a
            // Parameter pulled in by a binary op — e.g. UE's Schlick chain
            // `1 - clamp(ior, 1, 2)` leads with `Constant(1)`). Hand the
            // whole stream to the stack-machine evaluator first so we
            // produce semantic names (`ior_one_minus_clamp_ior_1_2`)
            // instead of collapsing to the bare parameter name.
            string? evaluatedFromNonParamLead = TryEvaluatePreshader(data, offset, size, parameters, preshaderNames, textureParameters);
            if (evaluatedFromNonParamLead != null) return evaluatedFromNonParamLead;
            string? recoveredFromNonParamLead = TryRecoverViaSingleParamScan(data, offset, size, parameters);
            if (recoveredFromNonParamLead != null) return recoveredFromNonParamLead;
            DumpPreshaderDebug(data, offset, size, parameters, byteOffset, materialPath, rows, "<nonParamLead>");
            return anonymous;
        }

        ushort paramIdx = BitConverter.ToUInt16(data, checked((int)offset + 1));
        if (paramIdx >= parameters.GetArrayLength())
        {
            return anonymous;
        }

        FMaterialParameterInfo? info = ParseMaterialParameterInfo(parameters[paramIdx]);
        if (info == null)
        {
            return anonymous;
        }
        string baseName = info.Name;

        if (size == 3)
        {
            return baseName;
        }

        int rest = checked((int)offset + 3);
        int restSize = checked((int)size) - 3;
        if (rest >= data.Length || restSize <= 0)
        {
            return anonymous;
        }
        byte tailOp = data[rest];

        // Parameter; ComponentSwizzle   (size 3 + 6 = 9)  -> <param>_<xyzw>
        if (tailOp == 36 && restSize == 6 && rest + 6 <= data.Length)
        {
            string swizzle = SwizzleSuffix(data[rest + 1], data[rest + 2], data[rest + 3], data[rest + 4], data[rest + 5]);
            return !string.IsNullOrEmpty(swizzle) ? $"{baseName}_{swizzle}" : anonymous;
        }

        // Parameter; ComponentSwizzle; <unary>   (size 3 + 6 + 1 = 10)  -> <param>_<xyzw>_<op>
        if (tailOp == 36 && restSize == 7 && rest + 7 <= data.Length)
        {
            string swizzle = SwizzleSuffix(data[rest + 1], data[rest + 2], data[rest + 3], data[rest + 4], data[rest + 5]);
            string? unary = MapUnaryOp(data[rest + 6]);
            if (!string.IsNullOrEmpty(swizzle) && unary != null)
            {
                return $"{baseName}_{swizzle}_{unary}";
            }
        }

        // Parameter; <unary>   (size 3 + 1 = 4)  -> <param>_<op>
        if (restSize == 1)
        {
            string? unary = MapUnaryOp(tailOp);
            if (unary != null)
            {
                return $"{baseName}_{unary}";
            }
        }

        // Parameter; Parameter; <binary>   (size 3 + 3 + 1 = 7)  -> <a>_<op>_<b>
        // Covers Add(4), Sub(5), Mul(6), Div(7), Fmod(8), Min(9), Max(10),
        // Atan2(18), Dot(19), Cross(20), AppendVector(37), Less(49),
        // Greater(51), LessEqual(52), GreaterEqual(53) — every leaf-binary
        // shape UE emits when a material expression collapses to a single
        // (paramA op paramB) operation. Higher-arity shapes stay anonymous
        // by design (the runtime VM stack state isn't a 1:1 name preserver).
        if (restSize == 4 && data[rest] == 3 && rest + 4 <= data.Length)
        {
            ushort otherIdx = BitConverter.ToUInt16(data, rest + 1);
            byte binaryOp = data[rest + 3];
            string? binary = MapBinaryOp(binaryOp);
            if (binary != null && otherIdx < parameters.GetArrayLength())
            {
                FMaterialParameterInfo? otherInfo = ParseMaterialParameterInfo(parameters[otherIdx]);
                if (otherInfo != null)
                {
                    return $"{baseName}_{binary}_{otherInfo.Name}";
                }
            }
        }

        // Parameter; Swizzle(.xyz); Parameter(same); Swizzle(.w); AppendVector
        // Identity round-trip (size 3 + 6 + 3 + 6 + 1 = 19 bytes total, restSize == 16).
        //
        // UE's HLSLMaterialTranslator emits this whole-vector reconstruction
        // when the material expression evaluates a Float4 parameter without a
        // trailing per-component swizzle. Semantically it's just `<param>`.
        // The unique-name deduplicator downstream rewrites collisions as
        // `<param>_at_<offset>` so the canonical-slot vs. preshader-reconstructed
        // slot stay distinct in the final cbuffer dump.
        if (restSize == 16 && rest + 16 <= data.Length
            && data[rest] == 36 && data[rest + 1] == 3 && data[rest + 2] == 0 && data[rest + 3] == 1 && data[rest + 4] == 2 /* .xyz */
            && data[rest + 6] == 3
            && BitConverter.ToUInt16(data, rest + 7) == paramIdx
            && data[rest + 9] == 36 && data[rest + 10] == 1 && data[rest + 11] == 3 /* .w */
            && data[rest + 15] == 37 /* AppendVector */)
        {
            return baseName;
        }

        // Parameter; Swizzle(.xyz); Parameter(same); Swizzle(.w); AppendVector; Swizzle(<final>)
        // (size 3 + 6 + 3 + 6 + 1 + 6 = 25 bytes total, restSize == 22)
        //
        // UE's HLSLMaterialTranslator round-trips a float4 parameter through
        // an xyz/w decomposition + AppendVector reconstruction before the
        // final swizzle. The whole chain is semantically `<paramName>.<final>`.
        // This shape produces ~50% of the previously-anonymous Material_f_<N>
        // slots in Oni_Valley_VFX (every `<Tex>_OffsetScale_xy` / `_zw`
        // texture-coordinate transform splits this way).
        //
        // Strictness: require the second `Parameter` to point at the same
        // index as the leading one (otherwise it's not a self-round-trip and
        // the final swizzle's `_<x>` suffix would be misleading). The
        // intermediate Swizzle(.xyz) and Swizzle(.w) just unpack/repack the
        // float4 — only the FINAL ComponentSwizzle determines which
        // components feed the slot.
        if (restSize == 22 && rest + 22 <= data.Length
            && data[rest] == 36 && data[rest + 1] == 3 && data[rest + 2] == 0 && data[rest + 3] == 1 && data[rest + 4] == 2 /* xyz */
            && data[rest + 6] == 3
            && BitConverter.ToUInt16(data, rest + 7) == paramIdx
            && data[rest + 9] == 36 && data[rest + 10] == 1 && data[rest + 11] == 3 /* .w */
            && data[rest + 15] == 37 /* AppendVector */
            && data[rest + 16] == 36 /* final ComponentSwizzle */)
        {
            string swizzle = SwizzleSuffix(data[rest + 17], data[rest + 18], data[rest + 19], data[rest + 20], data[rest + 21]);
            if (!string.IsNullOrEmpty(swizzle))
            {
                return $"{baseName}_{swizzle}";
            }
        }

        // Primary fallback: walk the byte stream as a stack machine and
        // synthesise an operation-aware identifier. Each stack value carries
        // a string name; binary/unary opcodes pop, then push the combined
        // name. The final TOS (top-of-stack) string is the slot's semantic
        // identity. See `TryEvaluatePreshader` below for the full evaluator.
        // This recognises the Schlick-F0-from-IOR chain UE emits whenever
        // a material expression touches `ior` indirectly — six distinct
        // expressions sharing the same lead parameter that previously
        // collapsed to `Material_ior_at_<offset>` now decode to
        // `Material_ior_clamp_1_2`, `Material_ior_one_minus_clamp_1_2`, etc.
        string? evaluated = TryEvaluatePreshader(data, offset, size, parameters, preshaderNames, textureParameters);
        if (evaluated != null)
        {
            return evaluated;
        }

        // Truly last-resort (when the evaluator hit an unknown opcode):
        // if the entire byte stream references exactly one Parameter, the
        // slot is some derived expression of that parameter — better to
        // name it after the parameter than leave it as an opaque `f_<N>`.
        string? recovered = TryRecoverViaSingleParamScan(data, offset, size, parameters);
        if (recovered != null)
        {
            return recovered;
        }

        DumpPreshaderDebug(data, offset, size, parameters, byteOffset, materialPath, rows, baseName);
        return anonymous;
    }

    // Walks the preshader byte stream as a stack machine and produces an
    // HLSL-identifier-friendly name for the final TOS value. Each stack
    // slot is just a string (`StackVal { Name, IsParam, IsConst, ConstLiteral }`):
    // binary ops pop two, push `<op>_<a>_<b>`; unary ops pop one, push
    // `<op>_<x>`; ComponentSwizzle pops one, pushes `<x>_<swizzle>`.
    //
    // The result is prefixed with the FIRST parameter referenced (so that
    // the synthesised member sits next to other uses of that parameter in
    // the alphabetised cbuffer dump). Constants-only chains return null —
    // those don't carry per-material semantics worth synthesising for.
    //
    // Idioms recognised (compact rewrites instead of nested names):
    //   sub_1_<x>     → <x>_one_minus
    //   mul_<x>_<x>   → <x>_sq        (square)
    //   clamp_<x>_1_2 → <x>_clamp_1_2 (UE's IOR-clamp idiom, kept readable)
    //
    // Bails out (returns null) when the byte stream is malformed, refers
    // to a parameter index that's out-of-range, runs the stack into an
    // empty state, or encounters an opcode with unknown operand size.
    private static string? TryEvaluatePreshader(byte[] data, uint offset, uint size, JsonElement parameters, string[]? preshaderNames = null, JsonElement textureParameters = default)
    {
        int n = checked((int)size);
        int dataStart = checked((int)offset);
        if (dataStart < 0 || dataStart > data.Length) return null;
        if (dataStart + n > data.Length) n = data.Length - dataStart;
        if (n < 1) return null;

        Stack<StackVal> stack = new();
        string? firstParamName = null;
        int? firstExternalId = null;

        int i = 0;
        while (i < n)
        {
            byte rawOp = data[dataStart + i];
            // Translate to the UE 5.1 canonical opcode the switch knows
            // about. For UE 5.0-5.3 this is the identity. See
            // `UeMaterialPreshaderVersion` for the per-version diffs.
            byte op = TranslateOpcode(rawOp);
            i++;

            switch (op)
            {
                case 0: // Nop
                    break;

                case 1: // ConstantZero
                    stack.Push(StackVal.Const("0"));
                    break;

                case 2: // Constant: 1 type byte + payload
                {
                    if (i >= n) return null;
                    byte ctype = data[dataStart + i];
                    int valueBytes = ctype switch
                    {
                        1 => 4,
                        2 => 8,
                        3 => 12,
                        4 => 16,
                        _ => -1,
                    };
                    if (valueBytes < 0) return null;
                    if (i + 1 + valueBytes > n) return null;
                    // For Float1 we render the actual literal so that
                    // constants like 1.0, 2.0, 0.08 round-trip into stable
                    // identifiers — that's what powers the IOR-clamp
                    // recognition. Float2/3/4 don't get literalised
                    // (they're rare and the per-component names would
                    // blow up the identifier length).
                    string lit;
                    if (ctype == 1)
                    {
                        float v = BitConverter.ToSingle(data, dataStart + i + 1);
                        lit = FormatConstLiteral(v);
                    }
                    else
                    {
                        lit = "k" + ctype;
                    }
                    stack.Push(StackVal.Const(lit));
                    i += 1 + valueBytes;
                    break;
                }

                case 3: // Parameter: u16 operand
                {
                    if (i + 2 > n) return null;
                    ushort idx = BitConverter.ToUInt16(data, dataStart + i);
                    if (idx >= parameters.GetArrayLength()) return null;
                    FMaterialParameterInfo? info = ParseMaterialParameterInfo(parameters[idx]);
                    if (info == null || string.IsNullOrEmpty(info.Name)) return null;
                    string pname = SanitizeIdent(info.Name);
                    firstParamName ??= pname;
                    stack.Push(StackVal.Param(pname));
                    i += 2;
                    break;
                }

                case 36: // ComponentSwizzle: numE + 4 component indices
                {
                    if (i + 5 > n) return null;
                    string swizzle = SwizzleSuffix(
                        data[dataStart + i + 0],
                        data[dataStart + i + 1],
                        data[dataStart + i + 2],
                        data[dataStart + i + 3],
                        data[dataStart + i + 4]);
                    i += 5;
                    if (stack.Count == 0) return null;
                    StackVal x = stack.Pop();
                    if (string.IsNullOrEmpty(swizzle))
                    {
                        stack.Push(x);
                    }
                    else
                    {
                        stack.Push(StackVal.Expr($"{x.Name}_{swizzle}"));
                    }
                    break;
                }

                case 37: // AppendVector (binary)
                {
                    if (stack.Count < 2) return null;
                    StackVal b = stack.Pop();
                    StackVal a = stack.Pop();
                    stack.Push(StackVal.Expr($"append_{a.Name}_{b.Name}"));
                    break;
                }

                // Texture-info family (UE 5.1 EPreshaderOpcode 38-42). Operand
                // sizes per `Preshader.cpp:GetTextureParameter` etc.:
                //   38 TextureSize / 39 TexelSize:
                //       FHashedMaterialParameterInfo (FScriptName u16 + int32 +
                //       u8 = 7 bytes) + int32 TextureIndex (4) = 11 bytes
                //   40 ExternalTextureCoordinateScaleRotation /
                //   41 ExternalTextureCoordinateOffset:
                //       FScriptName u16 (2) + FGuid (16) + int32 TextureIndex (4)
                //       = 22 bytes
                //   42 RuntimeVirtualTextureUniform:
                //       FHashedMaterialParameterInfo (7) + int32 TextureIndex (4)
                //       + int32 VectorIndex (4) = 15 bytes
                //
                // Mis-parsing the operand size desync'd the stream and forced
                // anonymity for every downstream slot. NOTE: opcode 38 was
                // previously mis-implemented here as ExternalInput (1-byte
                // operand) — UE 5.1 has no ExternalInput opcode at all.
                case 38: // TextureSize
                case 39: // TexelSize
                {
                    if (i + 11 > n) return null;
                    ushort nameIdx = BitConverter.ToUInt16(data, dataStart + i);
                    int textureIdx = BitConverter.ToInt32(data, dataStart + i + 7);
                    i += 11;
                    string texName = ResolveTextureName(nameIdx, textureIdx, preshaderNames, textureParameters);
                    firstParamName ??= texName;
                    stack.Push(StackVal.Expr($"{texName}_{(op == 38 ? "TextureSize" : "TexelSize")}"));
                    break;
                }

                case 42: // RuntimeVirtualTextureUniform
                {
                    if (i + 15 > n) return null;
                    ushort nameIdx = BitConverter.ToUInt16(data, dataStart + i);
                    int textureIdx = BitConverter.ToInt32(data, dataStart + i + 7);
                    int vectorIdx = BitConverter.ToInt32(data, dataStart + i + 11);
                    i += 15;
                    string texName = ResolveTextureName(nameIdx, textureIdx, preshaderNames, textureParameters);
                    firstParamName ??= texName;
                    stack.Push(StackVal.Expr($"{texName}_RVTUniform_{vectorIdx}"));
                    break;
                }

                case 40: // ExternalTextureCoordinateScaleRotation
                case 41: // ExternalTextureCoordinateOffset
                {
                    if (i + 22 > n) return null;
                    ushort nameIdx = BitConverter.ToUInt16(data, dataStart + i);
                    int textureIdx = BitConverter.ToInt32(data, dataStart + i + 18);
                    i += 22;
                    string texName = ResolveTextureName(nameIdx, textureIdx, preshaderNames, textureParameters);
                    firstParamName ??= texName;
                    stack.Push(StackVal.Expr($"{texName}_{(op == 40 ? "ExtTexCoordScaleRotation" : "ExtTexCoordOffset")}"));
                    break;
                }

                // Binary arithmetic / comparison.
                case 4: case 5: case 6: case 7: case 8:
                case 9: case 10:
                case 18: case 19: case 20:
                case 49: case 51: case 52: case 53:
                {
                    if (stack.Count < 2) return null;
                    StackVal b = stack.Pop();
                    StackVal a = stack.Pop();
                    stack.Push(StackVal.Expr(FormatBinary(op, a, b)));
                    break;
                }

                // Clamp (ternary): pops hi, lo, x.
                case 11:
                {
                    if (stack.Count < 3) return null;
                    StackVal hi = stack.Pop();
                    StackVal lo = stack.Pop();
                    StackVal x = stack.Pop();
                    stack.Push(StackVal.Expr(FormatClamp(x, lo, hi)));
                    break;
                }

                // Unary: Sin..Atan, Sqrt..Log10, Saturate, Abs, Floor..Frac, Neg.
                case 12: case 13: case 14: case 15: case 16: case 17:
                case 21: case 22: case 23: case 24: case 25: case 26:
                case 27: case 28: case 29: case 30: case 31: case 32:
                case 33: case 34: case 35:
                case 45:
                {
                    if (stack.Count < 1) return null;
                    string? uname = MapUnaryOp(op);
                    if (uname == null) return null;
                    StackVal x = stack.Pop();
                    stack.Push(StackVal.Expr($"{uname}_{x.Name}"));
                    break;
                }

                default:
                    // Unknown/variable-size opcode — abort and let the
                    // caller fall through to the single-param recovery.
                    return null;
            }
        }

        if (stack.Count == 0) return null;
        StackVal top = stack.Peek();

        string baseName = firstParamName ?? (firstExternalId.HasValue ? $"ext_{firstExternalId.Value}" : null!);
        string expr = top.Name;
        if (baseName == null)
        {
            // Pure-constant expression — not worth synthesising.
            return null;
        }

        // If the entire expression is just the lead parameter (e.g. a
        // round-trip Append chain reduced to `paramName`), return it
        // unchanged — the dedup layer adds `_at_<offset>` for collisions.
        // Otherwise compose as `<baseName>_<expr>`, but elide a redundant
        // inner repeat of baseName: an expression like `clamp_ior_1_2`
        // composed with baseName `ior` would otherwise produce
        // `ior_clamp_ior_1_2`; rewrite to `ior_clamp_1_2`.
        string composed;
        if (string.Equals(expr, baseName, StringComparison.Ordinal))
        {
            composed = baseName;
        }
        else if (expr.StartsWith(baseName + "_", StringComparison.Ordinal))
        {
            composed = expr;
        }
        else
        {
            string trimmed = ElideInnerBase(expr, baseName);
            composed = $"{baseName}_{trimmed}";
        }

        return TrimIdent(SanitizeIdent(composed));
    }

    // Rewrite `<op>_<base>_<rest>` → `<op>_<rest>` (and similar) so we
    // don't end up with `Material_ior_clamp_ior_1_2`. Only collapses
    // when the baseName appears as a discrete `_baseName_` token in the
    // expression — never inside another identifier (e.g. `iorBlend`).
    private static string ElideInnerBase(string expr, string baseName)
    {
        if (string.IsNullOrEmpty(baseName)) return expr;
        string needle = "_" + baseName + "_";
        int idx = expr.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) return expr;
        return expr.Substring(0, idx) + "_" + expr.Substring(idx + needle.Length);
    }

    private readonly struct StackVal
    {
        public readonly string Name;
        public readonly bool IsParam;
        public readonly bool IsConst;
        public readonly string? ConstLiteral;
        private StackVal(string name, bool isParam, bool isConst, string? lit)
        {
            Name = name; IsParam = isParam; IsConst = isConst; ConstLiteral = lit;
        }
        public static StackVal Param(string n) => new(n, true, false, null);
        public static StackVal Const(string lit) => new(lit, false, true, lit);
        public static StackVal Expr(string n) => new(n, false, false, null);
    }

    // Render a float constant as a stable, identifier-safe literal.
    // Whole numbers like 1.0, 2.0 → "1", "2". Fractional values like 0.08
    // → "0_08". Negatives → "neg0_5". Special-cases keep the IOR-clamp
    // idiom (`clamp_<x>_1_2`) recognisable.
    // Extracts the side table of FScriptNames stored alongside the preshader
    // bytecode. The bytecode references this table by uint16 index whenever
    // an opcode needs a parameter name (TextureSize / TexelSize / etc).
    // Returns an empty array if the JSON dump didn't surface the names.
    private static string[] ExtractPreshaderNames(JsonElement uniformPreshaderData)
    {
        if (uniformPreshaderData.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!uniformPreshaderData.TryGetProperty("Names", out JsonElement names) || names.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        List<string> result = new(names.GetArrayLength());
        foreach (JsonElement n in names.EnumerateArray())
        {
            result.Add(n.ValueKind == JsonValueKind.String ? (n.GetString() ?? "") : "");
        }
        return result.ToArray();
    }

    // Resolves an `(nameIdx, textureIdx)` pair from a Texture/Texel preshader
    // opcode into the most informative name available:
    //   1. preshaderNames[nameIdx] if it's a real parameter name (not "None")
    //   2. UniformTextureParameters[Standard2D][textureIdx].ParameterInfo.Name
    //   3. UniformTextureParameters[*][textureIdx].ParameterInfo.Name (search
    //      all type buckets — for TexelSize on a Cube/Volume/etc.)
    //   4. Falls back to `Texture_<idx>` if nothing names it
    private static string ResolveTextureName(ushort nameIdx, int textureIdx, string[]? preshaderNames, JsonElement textureParameters)
    {
        if (preshaderNames != null && nameIdx < preshaderNames.Length)
        {
            string n = preshaderNames[nameIdx];
            if (!string.IsNullOrEmpty(n) && !string.Equals(n, "None", StringComparison.Ordinal))
            {
                return SanitizeIdent(n);
            }
        }

        if (textureParameters.ValueKind == JsonValueKind.Array && textureIdx >= 0)
        {
            // Search each type bucket. Standard2D first (most common for TextureSize),
            // then Cube, Array2D, ArrayCube, Volume, Virtual, External.
            // Two known JSON shapes for the parameter name:
            //   * Cooked runtime shape (FMaterialUniformExpressionTextureParameter
            //     after RuntimeSerialize):
            //       { "ParameterName": "<name>", "Association": "...", "Index": <int>, ... }
            //   * Editor / per-material .uasset shape:
            //       { "ParameterInfo": { "Name": "<name>", "Index": <int>, "Association": "..." }, ... }
            // Both are present in the wild — the runtime path bakes the nested
            // FHashedMaterialParameterInfo into top-level fields, the editor
            // path keeps the FMaterialParameterInfo struct verbatim.
            for (int t = 0; t < textureParameters.GetArrayLength(); t++)
            {
                JsonElement bucket = textureParameters[t];
                if (bucket.ValueKind != JsonValueKind.Array) continue;
                if (textureIdx >= bucket.GetArrayLength()) continue;

                JsonElement entry = bucket[textureIdx];
                if (entry.ValueKind != JsonValueKind.Object) continue;

                string? name = null;
                if (entry.TryGetProperty("ParameterName", out JsonElement pn) && pn.ValueKind == JsonValueKind.String)
                {
                    name = pn.GetString();
                }
                else if (entry.TryGetProperty("ParameterInfo", out JsonElement pi)
                         && pi.ValueKind == JsonValueKind.Object
                         && pi.TryGetProperty("Name", out JsonElement nameEl)
                         && nameEl.ValueKind == JsonValueKind.String)
                {
                    name = nameEl.GetString();
                }

                if (!string.IsNullOrEmpty(name) && !string.Equals(name, "None", StringComparison.Ordinal))
                {
                    return SanitizeIdent(name);
                }
            }
        }

        return $"Texture_{textureIdx}";
    }

    /// <summary>
    /// **数值求值版的 preshader 解释器**(与 <see cref="TryEvaluatePreshader"/> 逐 opcode 同构,
    /// 只是栈里放的是 4 分量数值而不是标识符字符串)。
    ///
    /// 为什么必须有:名字版只能回答"这段 preshader 的**主导参数**叫什么",回答不了"这个 cbuffer
    /// 槽**等于多少**"。消费侧拿名字去材质里查参数值,对**恒等**表达式(槽 = 某个参数)是对的,
    /// 对**派生**表达式(槽 = 若干参数的运算结果)就取错值,而且两者名字长得一模一样、分不出来
    /// (实测某皮肤材质基色三元组的 R/G/B 分别叫 WW_RainWetAmount / WW_WetNoiseSize / …,
    /// 按名字取值渲出来是蓝的)。名字还原不出来的那些(<c>Unmapped_at_&lt;offset&gt;</c>)更是彻底没辙。
    ///
    /// 求值需要的东西**全都在同一份 UES JSON 里**:<c>UniformNumericParameters[i].Value</c> 就是
    /// 参数的真值(标量在 <c>R</c>,向量在 <c>R/G/B/A</c>),opcode 流在 <c>UniformPreshaderData.Data</c>。
    /// 所以这里不引入任何新数据源,只是把已有数据算完。
    ///
    /// 求不出来就返回 null(未知 opcode / 栈不平 / 取不到参数值),调用方照旧回落到名字路径 ——
    /// **算不出来要说算不出来,不能编一个值**。
    /// </summary>
    private static float[]? TryEvaluatePreshaderNumeric(byte[] data, uint offset, uint size, JsonElement parameters)
        => TryEvaluatePreshaderStack(data, offset, size, parameters) is { Count: > 0 } s ? s[^1] : null;

    /// <summary>
    /// 同 <see cref="TryEvaluatePreshaderNumeric"/>,但返回**整个栈**(栈底 → 栈顶)。
    /// 一段 opcode 可以一次算出多个值(<c>NumFields &gt; 1</c> 的 preshader 就是),
    /// 此时栈底到栈顶依次对应 field 0..N-1。
    /// </summary>
    private static List<float[]>? TryEvaluatePreshaderStack(byte[] data, uint offset, uint size, JsonElement parameters)
    {
        int n = checked((int)size);
        int dataStart = checked((int)offset);
        if (dataStart < 0 || dataStart > data.Length) return null;
        if (dataStart + n > data.Length) n = data.Length - dataStart;
        if (n < 1) return null;

        Stack<float[]> stack = new();
        int i = 0;
        while (i < n)
        {
            byte op = TranslateOpcode(data[dataStart + i]);
            i++;

            switch (op)
            {
                case 0: break;                                   // Nop
                case 1: stack.Push(new float[4]); break;         // ConstantZero

                case 2:                                          // Constant:类型字节 + 载荷
                {
                    if (i >= n) return null;
                    byte ctype = data[dataStart + i];
                    int comps = ctype switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, _ => -1 };
                    if (comps < 0 || i + 1 + comps * 4 > n) return null;
                    float[] v = new float[4];
                    for (int c = 0; c < comps; c++) v[c] = BitConverter.ToSingle(data, dataStart + i + 1 + c * 4);
                    // 标量常量按 HLSL 语义广播到各分量,后续与向量运算才对得上。
                    if (comps == 1) { v[1] = v[0]; v[2] = v[0]; v[3] = v[0]; }
                    stack.Push(v);
                    i += 1 + comps * 4;
                    break;
                }

                case 3:                                          // Parameter:u16 下标
                {
                    if (i + 2 > n) return null;
                    ushort idx = BitConverter.ToUInt16(data, dataStart + i);
                    if (idx >= parameters.GetArrayLength()) return null;
                    float[]? pv = ReadParameterValue(parameters[idx]);
                    if (pv == null) return null;
                    stack.Push(pv);
                    i += 2;
                    break;
                }

                case 36:                                         // ComponentSwizzle:numE + 4 个分量下标
                {
                    if (i + 5 > n) return null;
                    int numE = data[dataStart + i];
                    byte[] idxs = { data[dataStart + i + 1], data[dataStart + i + 2], data[dataStart + i + 3], data[dataStart + i + 4] };
                    i += 5;
                    if (stack.Count == 0) return null;
                    float[] x = stack.Pop();
                    float[] r = new float[4];
                    for (int c = 0; c < 4; c++)
                    {
                        int src = c < numE ? idxs[c] : idxs[numE > 0 ? numE - 1 : 0];
                        r[c] = src < 4 ? x[src] : 0f;
                    }
                    stack.Push(r);
                    break;
                }

                case 37:                                         // AppendVector
                {
                    if (stack.Count < 2) return null;
                    float[] b = stack.Pop();
                    float[] a = stack.Pop();
                    // 分量数在这份 JSON 里拿不到(要看字段类型),按 UE 最常见的
                    // `append(float3, float1)` 语义拼:a 占前三、b 的首分量补第四。
                    stack.Push(new[] { a[0], a[1], a[2], b[0] });
                    break;
                }

                case 4: case 5: case 6: case 7: case 8: case 9: case 10:
                case 18: case 19: case 20:
                case 49: case 51: case 52: case 53:
                {
                    if (stack.Count < 2) return null;
                    float[] b = stack.Pop();
                    float[] a = stack.Pop();
                    float[]? r = ApplyBinary(op, a, b);
                    if (r == null) return null;
                    stack.Push(r);
                    break;
                }

                case 11:                                         // Clamp(x, lo, hi)
                {
                    if (stack.Count < 3) return null;
                    float[] hi = stack.Pop();
                    float[] lo = stack.Pop();
                    float[] x = stack.Pop();
                    float[] r = new float[4];
                    for (int c = 0; c < 4; c++) r[c] = MathF.Min(MathF.Max(x[c], lo[c]), hi[c]);
                    stack.Push(r);
                    break;
                }

                case 12: case 13: case 14: case 15: case 16: case 17:
                case 21: case 22: case 23: case 24: case 25: case 26:
                case 27: case 28: case 29: case 30: case 31: case 32:
                case 33: case 34: case 35: case 45:
                {
                    if (stack.Count < 1) return null;
                    float[] x = stack.Pop();
                    float[]? r = ApplyUnary(op, x);
                    if (r == null) return null;
                    stack.Push(r);
                    break;
                }

                default:
                    return null;                                 // 未知/变长 opcode:不猜
            }
        }

        if (stack.Count == 0) return null;
        var bottomToTop = new List<float[]>(stack);   // Stack 的枚举序是栈顶 → 栈底
        bottomToTop.Reverse();
        return bottomToTop;
    }

    /// <summary>读 <c>UniformNumericParameters[i].Value</c>(标量只有 R 有意义,按 HLSL 广播)。</summary>
    private static float[]? ReadParameterValue(JsonElement parameter)
    {
        if (parameter.ValueKind != JsonValueKind.Object) return null;
        if (!parameter.TryGetProperty("Value", out JsonElement value)) return null;

        if (value.ValueKind == JsonValueKind.Number)
        {
            float f = value.GetSingle();
            return new[] { f, f, f, f };
        }
        if (value.ValueKind != JsonValueKind.Object) return null;

        float[] v = new float[4];
        string[] names = { "R", "G", "B", "A" };
        bool any = false;
        for (int c = 0; c < 4; c++)
        {
            if (value.TryGetProperty(names[c], out JsonElement comp) && comp.ValueKind == JsonValueKind.Number)
            {
                v[c] = comp.GetSingle();
                any = true;
            }
        }
        if (!any) return null;
        if (string.Equals(ReadString(parameter, "ParameterType"), "Scalar", StringComparison.Ordinal))
        {
            v[1] = v[0];
            v[2] = v[0];
            v[3] = v[0];
        }
        return v;
    }

    /// <summary>二元 opcode 的数值语义(与 <see cref="FormatBinary"/> 的命名表逐条对应)。</summary>
    private static float[]? ApplyBinary(byte op, float[] a, float[] b)
    {
        float[] r = new float[4];
        switch (op)
        {
            case 4: for (int c = 0; c < 4; c++) r[c] = a[c] + b[c]; return r;
            case 5: for (int c = 0; c < 4; c++) r[c] = a[c] - b[c]; return r;
            case 6: for (int c = 0; c < 4; c++) r[c] = a[c] * b[c]; return r;
            case 7: for (int c = 0; c < 4; c++) r[c] = b[c] != 0f ? a[c] / b[c] : 0f; return r;
            case 8: for (int c = 0; c < 4; c++) r[c] = b[c] != 0f ? a[c] - b[c] * MathF.Truncate(a[c] / b[c]) : 0f; return r;
            case 9: for (int c = 0; c < 4; c++) r[c] = MathF.Min(a[c], b[c]); return r;
            case 10: for (int c = 0; c < 4; c++) r[c] = MathF.Max(a[c], b[c]); return r;
            case 18: for (int c = 0; c < 4; c++) r[c] = MathF.Atan2(a[c], b[c]); return r;
            case 19:                                             // dot:标量结果,广播
            {
                float d = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
                return new[] { d, d, d, d };
            }
            case 20:                                             // cross(float3)
                return new[]
                {
                    a[1] * b[2] - a[2] * b[1],
                    a[2] * b[0] - a[0] * b[2],
                    a[0] * b[1] - a[1] * b[0],
                    0f,
                };
            case 49: for (int c = 0; c < 4; c++) r[c] = a[c] < b[c] ? 1f : 0f; return r;
            case 51: for (int c = 0; c < 4; c++) r[c] = a[c] > b[c] ? 1f : 0f; return r;
            case 52: for (int c = 0; c < 4; c++) r[c] = a[c] <= b[c] ? 1f : 0f; return r;
            case 53: for (int c = 0; c < 4; c++) r[c] = a[c] >= b[c] ? 1f : 0f; return r;
            default: return null;
        }
    }

    /// <summary>一元 opcode 的数值语义(与 <see cref="MapUnaryOp"/> 的命名表逐条对应)。</summary>
    private static float[]? ApplyUnary(byte op, float[] x)
    {
        float[] r = new float[4];
        switch (op)
        {
            case 12: for (int c = 0; c < 4; c++) r[c] = MathF.Sin(x[c]); return r;
            case 13: for (int c = 0; c < 4; c++) r[c] = MathF.Cos(x[c]); return r;
            case 14: for (int c = 0; c < 4; c++) r[c] = MathF.Tan(x[c]); return r;
            case 15: for (int c = 0; c < 4; c++) r[c] = MathF.Asin(x[c]); return r;
            case 16: for (int c = 0; c < 4; c++) r[c] = MathF.Acos(x[c]); return r;
            case 17: for (int c = 0; c < 4; c++) r[c] = MathF.Atan(x[c]); return r;
            case 21: for (int c = 0; c < 4; c++) r[c] = x[c] > 0f ? MathF.Sqrt(x[c]) : 0f; return r;
            case 22: for (int c = 0; c < 4; c++) r[c] = x[c] != 0f ? 1f / x[c] : 0f; return r;
            case 23:                                             // length:标量结果,广播
            {
                float l = MathF.Sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]);
                return new[] { l, l, l, l };
            }
            case 24:                                             // normalize(float3)
            {
                float l = MathF.Sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]);
                if (l <= 0f) return new float[4];
                return new[] { x[0] / l, x[1] / l, x[2] / l, x[3] };
            }
            case 25: for (int c = 0; c < 4; c++) r[c] = MathF.Min(MathF.Max(x[c], 0f), 1f); return r;
            case 26: for (int c = 0; c < 4; c++) r[c] = MathF.Abs(x[c]); return r;
            case 27: for (int c = 0; c < 4; c++) r[c] = MathF.Floor(x[c]); return r;
            case 28: for (int c = 0; c < 4; c++) r[c] = MathF.Ceiling(x[c]); return r;
            case 29: for (int c = 0; c < 4; c++) r[c] = MathF.Round(x[c]); return r;
            case 30: for (int c = 0; c < 4; c++) r[c] = MathF.Truncate(x[c]); return r;
            case 31: for (int c = 0; c < 4; c++) r[c] = MathF.Sign(x[c]); return r;
            case 32: case 33: for (int c = 0; c < 4; c++) r[c] = x[c] - MathF.Floor(x[c]); return r;
            case 34: for (int c = 0; c < 4; c++) r[c] = x[c] > 0f ? MathF.Log2(x[c]) : 0f; return r;
            case 35: for (int c = 0; c < 4; c++) r[c] = x[c] > 0f ? MathF.Log10(x[c]) : 0f; return r;
            case 45: for (int c = 0; c < 4; c++) r[c] = -x[c]; return r;
            default: return null;
        }
    }

    private static string FormatConstLiteral(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return "nan";
        // Whole-number short form.
        if (v == MathF.Truncate(v) && MathF.Abs(v) < 1e7f)
        {
            long iv = (long)v;
            return iv < 0 ? $"neg{-iv}" : iv.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // Fractional: format with up to 6 significant digits, replace dot.
        string raw = v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
        // Strip exponent notation gracefully — replace 'e' and '+' with safe chars.
        raw = raw.Replace('+', 'p').Replace('-', 'n').Replace('.', '_').Replace('e', 'E');
        return raw;
    }

    private static string FormatBinary(byte op, StackVal a, StackVal b)
    {
        string opName = op switch
        {
            4 => "add", 5 => "sub", 6 => "mul", 7 => "div", 8 => "fmod",
            9 => "min", 10 => "max",
            18 => "atan2", 19 => "dot", 20 => "cross",
            49 => "lt", 51 => "gt", 52 => "le", 53 => "ge",
            _ => "bin" + op,
        };

        // Idioms (UE BRDF/Schlick fingerprints):
        //   sub(1, x)  → x_one_minus     (`1 - foo`, ubiquitous in Fresnel)
        //   mul(x, x)  → x_sq             (square)
        //   div(x, x)  → x_self_div       (degenerate but stable name)
        if (op == 5 && a.IsConst && a.ConstLiteral == "1")
        {
            return $"{b.Name}_one_minus";
        }
        if (op == 6 && string.Equals(a.Name, b.Name, StringComparison.Ordinal))
        {
            return $"{a.Name}_sq";
        }
        return $"{opName}_{a.Name}_{b.Name}";
    }

    private static string FormatClamp(StackVal x, StackVal lo, StackVal hi)
    {
        // `clamp(<x>, 1, 2)` is the UE-IOR fingerprint — keep its literal
        // form so all six Schlick-F0 chain slots produce names that
        // surface the operation chain rather than collide.
        return $"clamp_{x.Name}_{lo.Name}_{hi.Name}";
    }

    private static string SanitizeIdent(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        System.Text.StringBuilder sb = new(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool valid = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
            sb.Append(valid ? c : '_');
        }
        // HLSL identifiers can't start with a digit.
        if (sb.Length > 0 && sb[0] >= '0' && sb[0] <= '9')
        {
            sb.Insert(0, '_');
        }
        // Collapse runs of underscores to a single underscore.
        System.Text.StringBuilder collapsed = new(sb.Length);
        bool prevUnderscore = false;
        for (int i = 0; i < sb.Length; i++)
        {
            char c = sb[i];
            if (c == '_')
            {
                if (!prevUnderscore) collapsed.Append('_');
                prevUnderscore = true;
            }
            else
            {
                collapsed.Append(c);
                prevUnderscore = false;
            }
        }
        return collapsed.ToString();
    }

    private static string TrimIdent(string s)
    {
        // Preshader (uniform-expression) slot names ARE the expression: the decompiled name is the
        // only surviving record of how that Material cbuffer slot is derived from author parameters.
        // Truncating it destroys that information irreversibly — a consumer can no longer re-evaluate
        // the slot and has to fall back to 0. Measured on Infinity Nikki S0165: the 80-char cap turned
        // names like `DissolveEdgeSoftness_sub_DissolveEdgeWidth_add_1_mul_...` into `..._etc` /
        // `DissolveEdgeSoftness_sub`, which left the dissolve mask unresolvable and rendered whole
        // garments black. HLSL identifiers have no practical length limit, so keep the full name and
        // only cap at a length that guards against pathological blow-ups.
        const int MaxLen = 512;
        if (s.Length <= MaxLen) return s;
        return s.Substring(0, MaxLen - 4) + "_etc";
    }

    // Walks the preshader byte stream opcode-by-opcode and returns the
    // referenced parameter's name when exactly one distinct Parameter
    // (opcode 3) is encountered. Used as a fallback when the structural
    // pattern matchers can't recognise the expression shape.
    //
    // Proper opcode walking (rather than byte-scanning for `0x03`) is
    // required to avoid false positives on bytes that happen to land at
    // value 3 inside a Constant's IEEE-754 mantissa. The walker only
    // needs to know the operand sizes of the opcodes that can appear
    // around a Parameter — Constant/ComponentSwizzle/Parameter — every
    // other opcode is treated as a single byte (correct for unary, binary,
    // and stack-only ops). If the walk runs into an unknown variable-size
    // opcode it bails out (returns null) rather than guess.
    private static string? TryRecoverViaSingleParamScan(byte[] data, uint offset, uint size, JsonElement parameters)
    {
        int n = checked((int)size);
        int dataStart = checked((int)offset);
        if (dataStart + n > data.Length) n = data.Length - dataStart;
        if (n < 3) return null;

        ushort? singleIdx = null;
        int i = 0;
        while (i < n)
        {
            // Translate cooked opcode to UE 5.1 canonical numbering — slots
            // 2/3 (Constant/Parameter) are unchanged across versions, but
            // 36 (ComponentSwizzle in 5.1) is Log10 in UE 5.7 and we MUST
            // NOT consume its 5-byte payload otherwise.
            byte op = TranslateOpcode(data[dataStart + i]);
            int operandBytes;

            if (op == 3) // Parameter: u16 operand
            {
                if (i + 1 + 2 > n) return null;
                ushort idx = BitConverter.ToUInt16(data, dataStart + i + 1);
                if (idx >= parameters.GetArrayLength()) return null;
                if (singleIdx.HasValue && singleIdx.Value != idx) return null;
                singleIdx = idx;
                operandBytes = 2;
            }
            else if (op == 2) // Constant: 1 type byte + value bytes
            {
                if (i + 1 >= n) return null;
                int valueBytes = data[dataStart + i + 1] switch
                {
                    1 => 4,   // Float
                    2 => 8,   // Float2
                    3 => 12,  // Float3
                    4 => 16,  // Float4
                    _ => -1,  // Unknown — abort walking
                };
                if (valueBytes < 0) return null;
                operandBytes = 1 + valueBytes;
            }
            else if (op == 36) // ComponentSwizzle: numE + 4 component indices
            {
                operandBytes = 5;
            }
            else if (op == 255)
            {
                // No-5.1-equivalent (Modulo / SparseVolumeTextureUniform /
                // Exp / Exp2 / Log) — we can't safely guess the operand
                // size, so abort the walk.
                return null;
            }
            else
            {
                // Unary / binary / stack-only ops: no operand.
                operandBytes = 0;
            }

            i += 1 + operandBytes;
        }

        if (!singleIdx.HasValue) return null;
        FMaterialParameterInfo? info = ParseMaterialParameterInfo(parameters[singleIdx.Value]);
        return string.IsNullOrEmpty(info?.Name) ? null : info!.Name;
    }

    private static void DumpPreshaderDebug(byte[] data, uint offset, uint size, JsonElement parameters, int byteOffset, string? materialPath, int rows, string baseName)
    {
        if (string.IsNullOrEmpty(PreshaderDebugFilter)) return;
        if (string.IsNullOrEmpty(materialPath) || materialPath.IndexOf(PreshaderDebugFilter, StringComparison.OrdinalIgnoreCase) < 0) return;
        int n = checked((int)size);
        int start = checked((int)offset);
        if (start + n > data.Length) n = data.Length - start;
        if (n <= 0) return;
        System.Text.StringBuilder sb = new();
        sb.Append("[preshader-debug] mat=").Append(System.IO.Path.GetFileName(materialPath))
          .Append(" cb=").Append(byteOffset)
          .Append(" kind=").Append(rows).Append("xN")
          .Append(" leadParam=").Append(baseName)
          .Append(" restSize=").Append(n - 3)
          .Append(" bytes=[");
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[start + i].ToString("X2"));
        }
        sb.Append("] refs=[");
        // Walk the byte stream looking for opcode 3 (Parameter) followed by a u16 idx,
        // resolve each to a parameter name from the parameters JsonElement.
        bool first = true;
        for (int i = 0; i + 3 <= n; i++)
        {
            if (data[start + i] != 3) continue;
            ushort idx = BitConverter.ToUInt16(data, start + i + 1);
            if (idx >= parameters.GetArrayLength()) continue;
            FMaterialParameterInfo? info = ParseMaterialParameterInfo(parameters[idx]);
            if (info == null) continue;
            if (!first) sb.Append(',');
            sb.Append('@').Append(i).Append(':').Append(info.Name);
            first = false;
        }
        sb.Append(']');
        Console.WriteLine(sb.ToString());
    }

    // Bytes match UE 5.1's `EPreshaderOpcode` (`Engine/Public/Shader/Preshader.h:19-75`):
    // Sin=12 Cos=13 Tan=14 Asin=15 Acos=16 Atan=17 Sqrt=21 Rcp=22
    // Length=23 Normalize=24 Saturate=25 Abs=26 Floor=27 Ceil=28
    // Round=29 Trunc=30 Sign=31 Frac=32 Fractional=33 Log2=34 Log10=35 Neg=45
    //
    // VERSION-SHIFT NOTE — opcodes are NOT stable across UE versions:
    //   * UE 5.4 inserts `SparseVolumeTextureUniform` at slot 43, pushing
    //     GetField/SetField/Neg/Jump/.../GreaterEqual up by +1, and appends
    //     Exp/Exp2/Log at slots 55-57.
    //   * UE 5.7 inserts `Modulo` at slot 9, shifting EVERY opcode at
    //     slot 9+ up by +1. So in 5.7, Min=10, Max=11, Clamp=12,
    //     Sin=13, ..., Log10=36, ComponentSwizzle=37, etc.
    //
    // This reader hardcodes the UE 5.1 layout. For cooks from UE 5.4+ the
    // unary/binary opcode case statements would mis-dispatch — but in
    // practice the default branch returns null on unknown opcodes, which
    // safely aborts the preshader stream rather than producing garbage
    // names. The lost name recovery shows up as anonymous Material_f_<N>
    // entries; a version-aware opcode table would close that gap. Left as
    // a future stage since the cooks tested so far (Oni_Valley_VFX = 5.1,
    // InfinityNikki = 5.4) haven't surfaced material expressions using the
    // shifted opcodes in the failure mode.
    private static string? MapUnaryOp(byte op) => op switch
    {
        12 => "sin",
        13 => "cos",
        14 => "tan",
        15 => "asin",
        16 => "acos",
        17 => "atan",
        21 => "sqrt",
        22 => "rcp",
        23 => "length",
        24 => "normalize",
        25 => "sat",
        26 => "abs",
        27 => "floor",
        28 => "ceil",
        29 => "round",
        30 => "trunc",
        31 => "sign",
        32 => "frac",
        33 => "fractional",
        34 => "log2",
        35 => "log10",
        45 => "neg",
        _ => null,
    };

    // Binary opcodes from `EPreshaderOpcode` — Add=4..GreaterEqual=53.
    // Names match HLSL intrinsic / verbose conventions so two paramNames
    // joined by them are unambiguous in the synthesised member name.
    private static string? MapBinaryOp(byte op) => op switch
    {
        4  => "add",
        5  => "sub",
        6  => "mul",
        7  => "div",
        8  => "fmod",
        9  => "min",
        10 => "max",
        18 => "atan2",
        19 => "dot",
        20 => "cross",
        37 => "append",
        49 => "lt",
        51 => "gt",
        52 => "le",
        53 => "ge",
        _ => null,
    };

    private enum FieldKind { Unknown, Float, LwcDouble, Int, Bool, Numeric, Float4x4, LwcDouble4x4 }

    private static FieldKind TryMapFieldType(string? fieldType, out int rows)
    {
        rows = 0;
        switch (fieldType)
        {
            case "Float1": rows = 1; return FieldKind.Float;
            case "Float2": rows = 2; return FieldKind.Float;
            case "Float3": rows = 3; return FieldKind.Float;
            case "Float4": rows = 4; return FieldKind.Float;
            case "Double1": rows = 1; return FieldKind.LwcDouble;
            case "Double2": rows = 2; return FieldKind.LwcDouble;
            case "Double3": rows = 3; return FieldKind.LwcDouble;
            case "Double4": rows = 4; return FieldKind.LwcDouble;
            case "Int1": rows = 1; return FieldKind.Int;
            case "Int2": rows = 2; return FieldKind.Int;
            case "Int3": rows = 3; return FieldKind.Int;
            case "Int4": rows = 4; return FieldKind.Int;
            case "Bool1": rows = 1; return FieldKind.Bool;
            case "Bool2": rows = 2; return FieldKind.Bool;
            case "Bool3": rows = 3; return FieldKind.Bool;
            case "Bool4": rows = 4; return FieldKind.Bool;
            case "Numeric1": rows = 1; return FieldKind.Numeric;
            case "Numeric2": rows = 2; return FieldKind.Numeric;
            case "Numeric3": rows = 3; return FieldKind.Numeric;
            case "Numeric4": rows = 4; return FieldKind.Numeric;
            case "Float4x4": rows = 4; return FieldKind.Float4x4;
            case "Double4x4": rows = 4; return FieldKind.LwcDouble4x4;
            default: return FieldKind.Unknown;
        }
    }

    private static FMaterialParameterInfo? ParseMaterialParameterInfo(JsonElement element)
    {
        JsonElement parameterInfo;
        bool nested;
        if (element.TryGetProperty("ParameterInfo", out parameterInfo) && parameterInfo.ValueKind == JsonValueKind.Object)
        {
            nested = true;
        }
        else
        {
            parameterInfo = element;
            nested = false;
        }

        string? name = nested
            ? ReadString(parameterInfo, "Name")
            : ReadString(parameterInfo, "ParameterName") ?? ReadString(parameterInfo, "Name");
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? associationRaw = ReadString(parameterInfo, "Association");
        EMaterialParameterAssociation association = associationRaw switch
        {
            "EMaterialParameterAssociation::LayerParameter" => EMaterialParameterAssociation.LayerParameter,
            "EMaterialParameterAssociation::BlendParameter" => EMaterialParameterAssociation.BlendParameter,
            "LayerParameter" => EMaterialParameterAssociation.LayerParameter,
            "BlendParameter" => EMaterialParameterAssociation.BlendParameter,
            _ => EMaterialParameterAssociation.GlobalParameter
        };

        int index = parameterInfo.TryGetProperty("Index", out JsonElement indexElement) && indexElement.ValueKind == JsonValueKind.Number
            ? indexElement.GetInt32()
            : -1;
        return new FMaterialParameterInfo(name, association, index);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static uint ReadUInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"Missing numeric property: {propertyName}");
        }

        return value.GetUInt32();
    }
}
