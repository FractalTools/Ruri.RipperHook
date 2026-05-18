using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.Game.SBUE.ShaderDecompiler;

internal static class MaterialConstantBufferReader
{
    public static ConstantBufferParameter? Read(JsonElement uniformExpressionSet)
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

            string baseName = DerivePreshaderName(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters, byteOffset);
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

    private static string DerivePreshaderName(byte[] data, uint offset, uint size, JsonElement parameters, int byteOffset)
    {
        string anonymous = $"f_{byteOffset}";
        if (size < 3 || offset >= (uint)data.Length || offset + 3 > (uint)data.Length)
        {
            return anonymous;
        }
        if (data[offset] != 3)
        {
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

        return anonymous;
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
