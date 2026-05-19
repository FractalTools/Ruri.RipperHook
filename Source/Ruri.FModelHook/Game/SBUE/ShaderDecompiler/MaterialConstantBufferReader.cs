using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class MaterialConstantBufferReader
{
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
            if (numFields != 1 || fieldIndex >= uniformPreshaderFields.GetArrayLength())
            {
                continue;
            }

            JsonElement field = uniformPreshaderFields[checked((int)fieldIndex)];
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
            switch (kind)
            {
                case FieldKind.Float:
                case FieldKind.Numeric:
                    AddVectorMember(vectorParams, RegisterUniqueName(seenNames, baseName, byteOffset), byteOffset, rows, ShaderParamType.Float);
                    break;
                case FieldKind.Int:
                    AddVectorMember(vectorParams, RegisterUniqueName(seenNames, baseName, byteOffset), byteOffset, rows, ShaderParamType.Int);
                    break;
                case FieldKind.Bool:
                    AddVectorMember(vectorParams, RegisterUniqueName(seenNames, baseName, byteOffset), byteOffset, rows, ShaderParamType.Bool);
                    break;
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
        if (seenNames.Add(candidate))
        {
            return candidate;
        }
        string disambiguated = $"{candidate}_at_{byteOffset}";
        seenNames.Add(disambiguated);
        return disambiguated;
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
            byte op = data[dataStart + i];
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
        const int MaxLen = 80;
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
            byte op = data[dataStart + i];
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

    // Bytes match `EPreshaderOpcode` (`Engine/Public/Shader/Preshader.h:19-75`):
    // Sin=12 Cos=13 Tan=14 Asin=15 Acos=16 Atan=17 Sqrt=21 Rcp=22
    // Length=23 Normalize=24 Saturate=25 Abs=26 Floor=27 Ceil=28
    // Round=29 Trunc=30 Sign=31 Frac=32 Fractional=33 Log2=34 Log10=35 Neg=45
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
