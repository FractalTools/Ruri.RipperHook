using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.ShaderDecompiler;

internal enum UeMaterialPreshaderVersion
{
    Ue51 = 51,    Ue54 = 54,    Ue55 = 55,}

internal static class MaterialConstantBufferReader
{
    public static UeMaterialPreshaderVersion PreshaderVersion { get; set; } = UeMaterialPreshaderVersion.Ue51;

    public static readonly Dictionary<string, Dictionary<string, string>> EvaluatedCbufferValues = new(StringComparer.Ordinal);

    public static readonly Dictionary<string, Dictionary<string, int>> EvaluatedCbufferOffsets = new(StringComparer.Ordinal);

    public static readonly Dictionary<string, Dictionary<string, string>> EvaluatedCbufferPrograms = new(StringComparer.Ordinal);

    public static readonly Dictionary<string, Dictionary<string, string>> EvaluatedCbufferParams = new(StringComparer.Ordinal);

    /// <summary>Per material, every preshader-filled field with the program that computes it, at its absolute byte offset in the buffer.</summary>
    public static readonly Dictionary<string, List<PreshaderField>> EvaluatedCbufferFields = new(StringComparer.Ordinal);

    private static void ResetMaterialTables(string materialPath)
    {
        if (string.IsNullOrEmpty(materialPath)) return;
        EvaluatedCbufferValues.Remove(materialPath);
        EvaluatedCbufferOffsets.Remove(materialPath);
        EvaluatedCbufferPrograms.Remove(materialPath);
        EvaluatedCbufferParams.Remove(materialPath);
        EvaluatedCbufferFields.Remove(materialPath);
    }

    /// <summary>
    /// The names of every numeric parameter a preshader program reads, walking the program
    /// by each opcode's operand size the way the evaluator does; a program with an opcode
    /// the evaluator does not know is walked up to it.
    /// </summary>
    private static IReadOnlyList<string> ReferencedParameters(byte[] data, uint offset, uint size, JsonElement parameters)
    {
        List<string> names = new();
        int n = checked((int)size);
        int dataStart = checked((int)offset);
        if (dataStart < 0 || dataStart > data.Length) return names;
        if (dataStart + n > data.Length) n = data.Length - dataStart;
        int i = 0;
        while (i < n)
        {
            byte op = TranslateOpcode(data[dataStart + i]);
            i++;
            switch (op)
            {
                case 2:
                {
                    if (i >= n) return names;
                    int valueBytes = data[dataStart + i] switch { 1 => 4, 2 => 8, 3 => 12, 4 => 16, _ => -1 };
                    if (valueBytes < 0) return names;
                    i += 1 + valueBytes;
                    break;
                }
                case 3:
                {
                    if (i + 2 > n) return names;
                    ushort idx = BitConverter.ToUInt16(data, dataStart + i);
                    i += 2;
                    if (idx < parameters.GetArrayLength() && ParseMaterialParameterInfo(parameters[idx]) is { } info && !string.IsNullOrEmpty(info.Name) && !names.Contains(info.Name))
                    {
                        names.Add(info.Name);
                    }
                    break;
                }
                case 36:
                    i += 5;
                    break;
                case 38:
                case 39:
                    i += 11;
                    break;
                case 40:
                case 41:
                    i += 22;
                    break;
                case 42:
                    i += 15;
                    break;
                case <= 37:
                    break;
                default:
                    return names;
            }
        }
        return names;
    }

    private static void RecordField(string? materialPath, PreshaderField field)
    {
        if (string.IsNullOrEmpty(materialPath)) return;
        if (!EvaluatedCbufferFields.TryGetValue(materialPath, out List<PreshaderField>? fields))
        {
            EvaluatedCbufferFields[materialPath] = fields = new List<PreshaderField>();
        }
        fields.Add(field);
    }

    /// <summary>
    /// A field's value for the given numeric parameters -- the same parameter array the
    /// expression set carries, with any parameter's <c>Value</c> replaced by what a material
    /// instance sets it to -- computed by the field's own preshader program.
    /// </summary>
    public static float[]? Evaluate(JsonElement uniformExpressionSet, PreshaderField field, JsonElement numericParameters)
    {
        if (!uniformExpressionSet.TryGetProperty("UniformPreshaderData", out JsonElement uniformPreshaderData)
            || uniformPreshaderData.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        string? encodedData = ReadString(uniformPreshaderData, "Data");
        if (string.IsNullOrWhiteSpace(encodedData))
        {
            return null;
        }
        List<float[]>? stackValues = TryEvaluatePreshaderStack(Convert.FromBase64String(encodedData), field.OpcodeOffset, field.OpcodeSize, numericParameters);
        if (stackValues == null)
        {
            return null;
        }
        return field.NumFields == 1 ? stackValues[^1]
            : stackValues.Count == field.NumFields ? stackValues[field.FieldSlot]
            : null;
    }

    private static void RecordParams(string materialPath, JsonElement parameters)
    {
        if (string.IsNullOrEmpty(materialPath) || parameters.ValueKind != JsonValueKind.Array) return;

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement parameter in parameters.EnumerateArray())
        {
            FMaterialParameterInfo? info = ParseMaterialParameterInfo(parameter);
            if (info == null || string.IsNullOrEmpty(info.Name)) continue;
            float[]? v = ReadParameterValue(parameter);
            if (v == null) continue;
            table[info.Name] = string.Join(",", v.Select(static c => c.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (table.Count > 0) EvaluatedCbufferParams[materialPath] = table;
    }

    private static void RecordEvaluated(string materialPath, string memberName, int rows, float[]? value, int byteOffset, string? program = null)
    {
        if (!string.IsNullOrEmpty(materialPath) && !string.IsNullOrEmpty(program))
        {
            if (!EvaluatedCbufferPrograms.TryGetValue(materialPath, out Dictionary<string, string>? programs))
            {
                programs = new Dictionary<string, string>(StringComparer.Ordinal);
                EvaluatedCbufferPrograms[materialPath] = programs;
            }

            programs[memberName] = program;
        }

        if (!EvaluatedCbufferOffsets.TryGetValue(materialPath, out Dictionary<string, int>? offsets))
        {
            offsets = new Dictionary<string, int>(StringComparer.Ordinal);
            EvaluatedCbufferOffsets[materialPath] = offsets;
        }
        offsets[memberName] = byteOffset;

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
                if (raw <= 42) return raw;                if (raw == 43) return 255;                if (raw <= 54) return (byte)(raw - 1);                return 255;
            case UeMaterialPreshaderVersion.Ue55:
                if (raw <= 8) return raw;                if (raw == 9) return 255;                if (raw <= 43) return (byte)(raw - 1);                if (raw == 44) return 255;                if (raw <= 55) return (byte)(raw - 2);                return 255;        }
        return raw;
    }

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

        string[] preshaderNames = ExtractPreshaderNames(uniformPreshaderData);
        JsonElement uniformTextureParameters = default;
        uniformExpressionSet.TryGetProperty("UniformTextureParameters", out uniformTextureParameters);

        (int preshaderBufferStart, int vtPageTableBytes, int vtUniformBytes, int numericRegionEnd) = ComputeNumericLayout(uniformExpressionSet, (int)constantBufferSize);

        ResetMaterialTables(materialPath ?? string.Empty);
        RecordParams(materialPath ?? string.Empty, uniformNumericParameters);

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
            if (numFields < 1 || fieldIndex + numFields > (uint)uniformPreshaderFields.GetArrayLength())
            {
                continue;
            }

            List<float[]>? stackValues = TryEvaluatePreshaderStack(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters);

            TryEvaluatePreshader(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters,
                out string? opcodeProgram, preshaderNames, uniformTextureParameters);

            for (uint fieldSlot = 0; fieldSlot < numFields; fieldSlot++)
            {
            JsonElement field = uniformPreshaderFields[checked((int)(fieldIndex + fieldSlot))];
            string? rawFieldType = ReadString(field, "Type");
            FieldKind kind = TryMapFieldType(rawFieldType, out int rows);
            if (kind == FieldKind.Unknown)
            {
                if (Environment.GetEnvironmentVariable("RURI_PRESHADER_DEBUG") == "1")
                {
                    Console.WriteLine($"[preshader-field] 未识别字段类型 '{rawFieldType}' @cb={preshaderBufferStart + checked((int)ReadUInt32(field, "BufferOffset") * 4)} mat={materialPath}");
                }
                continue;
            }

            int byteOffset = preshaderBufferStart + checked((int)ReadUInt32(field, "BufferOffset") * 4);
            if (!seenOffsets.Add(byteOffset))
            {
                if (Environment.GetEnvironmentVariable("RURI_PRESHADER_DEBUG") == "1")
                {
                    Console.WriteLine($"[preshader-dup] 偏移 {byteOffset}(= [{byteOffset / 16}][{byteOffset % 16 / 4}])被重复写,丢弃后来者 mat={materialPath}");
                }
                continue;
            }

            string baseName = DerivePreshaderName(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters, byteOffset, materialPath, rows, preshaderNames, uniformTextureParameters);
            if (numFields > 1) baseName = $"{baseName}_f{fieldSlot}";

            float[]? evaluated = stackValues == null ? null
                : numFields == 1 ? stackValues[^1]
                : stackValues.Count == numFields ? stackValues[checked((int)fieldSlot)]
                : null;
            DumpPreshaderDebug(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters, byteOffset, materialPath, rows, baseName);
            switch (kind)
            {
                case FieldKind.Float:
                case FieldKind.Numeric:
                {
                    string memberName = RegisterUniqueName(seenNames, baseName, byteOffset);
                    RecordEvaluated(materialPath, memberName, rows, evaluated, byteOffset - preshaderBufferStart, opcodeProgram);
                    RecordField(materialPath, new PreshaderField(memberName, byteOffset, rows, opcodeOffset, opcodeSize, checked((int)fieldSlot), checked((int)numFields), opcodeProgram, ReferencedParameters(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters)));
                    for (int comp = 1; comp < rows; comp++) seenOffsets.Add(byteOffset + comp * 4);
                    AddVectorMember(vectorParams, memberName, byteOffset, rows, ShaderParamType.Float);
                    break;
                }
                case FieldKind.Int:
                {
                    string memberName = RegisterUniqueName(seenNames, baseName, byteOffset);
                    RecordEvaluated(materialPath, memberName, rows, evaluated, byteOffset - preshaderBufferStart, opcodeProgram);
                    RecordField(materialPath, new PreshaderField(memberName, byteOffset, rows, opcodeOffset, opcodeSize, checked((int)fieldSlot), checked((int)numFields), opcodeProgram, ReferencedParameters(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters)));
                    for (int comp = 1; comp < rows; comp++) seenOffsets.Add(byteOffset + comp * 4);
                    AddVectorMember(vectorParams, memberName, byteOffset, rows, ShaderParamType.Int);
                    break;
                }
                case FieldKind.Bool:
                {
                    string memberName = RegisterUniqueName(seenNames, baseName, byteOffset);
                    RecordEvaluated(materialPath, memberName, rows, evaluated, byteOffset - preshaderBufferStart, opcodeProgram);
                    RecordField(materialPath, new PreshaderField(memberName, byteOffset, rows, opcodeOffset, opcodeSize, checked((int)fieldSlot), checked((int)numFields), opcodeProgram, ReferencedParameters(opcodeData, opcodeOffset, opcodeSize, uniformNumericParameters)));
                    for (int comp = 1; comp < rows; comp++) seenOffsets.Add(byteOffset + comp * 4);
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
                    for (int comp = 1; comp < 16; comp++) seenOffsets.Add(byteOffset + comp * 4);
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

        bool preshaderAsArray = Environment.GetEnvironmentVariable("RURI_PRESHADER_AS_ARRAY") != "0";
        if (preshaderAsArray && numericRegionEnd > preshaderBufferStart)
        {
            vectorParams.RemoveAll(v => v.Index >= preshaderBufferStart && v.Index < numericRegionEnd);
            matrixParams.RemoveAll(m => m.Index >= preshaderBufferStart && m.Index < numericRegionEnd);
            vectorParams.Add(new VectorParameter
            {
                Name = "PreshaderBuffer",
                NameIndex = -1,
                Type = ShaderParamType.Float,
                Index = preshaderBufferStart,
                ArraySize = (numericRegionEnd - preshaderBufferStart) / 16,
                IsMatrix = false,
                RowCount = 4,
                ColumnCount = 1,
            });
        }

        bool fillGaps = Environment.GetEnvironmentVariable("RURI_PRESHADER_FILL_GAPS") == "1";
        for (int gapOffset = fillGaps ? preshaderBufferStart : numericRegionEnd; gapOffset + 4 <= numericRegionEnd; gapOffset += 4)
        {
            if (!seenOffsets.Add(gapOffset)) continue;
            string gapName = RegisterUniqueName(seenNames, $"Unmapped_at_{gapOffset}", gapOffset);
            AddVectorMember(vectorParams, gapName, gapOffset, 1, ShaderParamType.Float);
        }

        if (vectorParams.Count == 0 && matrixParams.Count == 0)
        {
            return null;
        }

        materialBuffer.VectorParameters = vectorParams.OrderBy(static p => p.Index).ToArray();
        materialBuffer.MatrixParameters = matrixParams.OrderBy(static p => p.Index).ToArray();
        return materialBuffer;
    }

    private static (int preshaderBufferStart, int vtPageTableBytes, int vtUniformBytes, int numericEnd) ComputeNumericLayout(JsonElement uniformExpressionSet, int constantBufferSize)
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

        if (int.TryParse(Environment.GetEnvironmentVariable("RURI_PRESHADER_OFFSET_DELTA"), out int delta))
        {
            preshaderBufferStart += delta;
        }

        return (preshaderBufferStart, vtPageTableBytes, vtUniformBytes, numericEnd);
    }

    private static string RegisterUniqueName(HashSet<string> seenNames, string candidate, int byteOffset)
    {
        string sanitized = SanitizeHlslIdent(candidate);
        if (string.IsNullOrEmpty(sanitized)) sanitized = $"f_{byteOffset}";
        if (seenNames.Add(sanitized)) return sanitized;
        string disambiguated = $"{sanitized}_at_{byteOffset}";
        seenNames.Add(disambiguated);
        return disambiguated;
    }

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

        if (tailOp == 36 && restSize == 6 && rest + 6 <= data.Length)
        {
            string swizzle = SwizzleSuffix(data[rest + 1], data[rest + 2], data[rest + 3], data[rest + 4], data[rest + 5]);
            return !string.IsNullOrEmpty(swizzle) ? $"{baseName}_{swizzle}" : anonymous;
        }

        if (tailOp == 36 && restSize == 7 && rest + 7 <= data.Length)
        {
            string swizzle = SwizzleSuffix(data[rest + 1], data[rest + 2], data[rest + 3], data[rest + 4], data[rest + 5]);
            string? unary = MapUnaryOp(data[rest + 6]);
            if (!string.IsNullOrEmpty(swizzle) && unary != null)
            {
                return $"{baseName}_{swizzle}_{unary}";
            }
        }

        if (restSize == 1)
        {
            string? unary = MapUnaryOp(tailOp);
            if (unary != null)
            {
                return $"{baseName}_{unary}";
            }
        }

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

        if (restSize == 16 && rest + 16 <= data.Length
            && data[rest] == 36 && data[rest + 1] == 3 && data[rest + 2] == 0 && data[rest + 3] == 1 && data[rest + 4] == 2            && data[rest + 6] == 3
            && BitConverter.ToUInt16(data, rest + 7) == paramIdx
            && data[rest + 9] == 36 && data[rest + 10] == 1 && data[rest + 11] == 3            && data[rest + 15] == 37)
        {
            return baseName;
        }

        if (restSize == 22 && rest + 22 <= data.Length
            && data[rest] == 36 && data[rest + 1] == 3 && data[rest + 2] == 0 && data[rest + 3] == 1 && data[rest + 4] == 2            && data[rest + 6] == 3
            && BitConverter.ToUInt16(data, rest + 7) == paramIdx
            && data[rest + 9] == 36 && data[rest + 10] == 1 && data[rest + 11] == 3            && data[rest + 15] == 37            && data[rest + 16] == 36)
        {
            string swizzle = SwizzleSuffix(data[rest + 17], data[rest + 18], data[rest + 19], data[rest + 20], data[rest + 21]);
            if (!string.IsNullOrEmpty(swizzle))
            {
                return $"{baseName}_{swizzle}";
            }
        }

        string? evaluated = TryEvaluatePreshader(data, offset, size, parameters, preshaderNames, textureParameters);
        if (evaluated != null)
        {
            return evaluated;
        }

        string? recovered = TryRecoverViaSingleParamScan(data, offset, size, parameters);
        if (recovered != null)
        {
            return recovered;
        }

        DumpPreshaderDebug(data, offset, size, parameters, byteOffset, materialPath, rows, baseName);
        return anonymous;
    }

    private static string? TryEvaluatePreshader(byte[] data, uint offset, uint size, JsonElement parameters, string[]? preshaderNames = null, JsonElement textureParameters = default)
        => TryEvaluatePreshader(data, offset, size, parameters, out _, preshaderNames, textureParameters);

    private static string? TryEvaluatePreshader(byte[] data, uint offset, uint size, JsonElement parameters, out string? program, string[]? preshaderNames = null, JsonElement textureParameters = default)
    {
        program = null;
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
            byte op = TranslateOpcode(rawOp);
            i++;

            switch (op)
            {
                case 0:                    break;

                case 1:                    stack.Push(StackVal.Const("0", "c1:0"));
                    break;

                case 2:                {
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
                    stack.Push(StackVal.Const(lit, ConstProgram(data, dataStart + i + 1, ctype)));
                    i += 1 + valueBytes;
                    break;
                }

                case 3:                {
                    if (i + 2 > n) return null;
                    ushort idx = BitConverter.ToUInt16(data, dataStart + i);
                    if (idx >= parameters.GetArrayLength()) return null;
                    FMaterialParameterInfo? info = ParseMaterialParameterInfo(parameters[idx]);
                    if (info == null || string.IsNullOrEmpty(info.Name)) return null;
                    string pname = SanitizeIdent(info.Name);
                    firstParamName ??= pname;
                    stack.Push(StackVal.Param(pname, "p" + ParameterComponentCount(parameters[idx]) + ProgramQuote(info.Name)));
                    i += 2;
                    break;
                }

                case 36:                {
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
                        stack.Push(StackVal.Expr($"{x.Name}_{swizzle}", $"(swz{swizzle} {x.Program})"));
                    }
                    break;
                }

                case 37:                {
                    if (stack.Count < 2) return null;
                    StackVal b = stack.Pop();
                    StackVal a = stack.Pop();
                    stack.Push(StackVal.Expr($"append_{a.Name}_{b.Name}", $"(append {a.Program} {b.Program})"));
                    break;
                }

                case 38:                case 39:                {
                    if (i + 11 > n) return null;
                    ushort nameIdx = BitConverter.ToUInt16(data, dataStart + i);
                    int textureIdx = BitConverter.ToInt32(data, dataStart + i + 7);
                    i += 11;
                    string texName = ResolveTextureName(nameIdx, textureIdx, preshaderNames, textureParameters);
                    firstParamName ??= texName;
                    stack.Push(StackVal.Expr($"{texName}_{(op == 38 ? "TextureSize" : "TexelSize")}", "x" + ProgramQuote($"{texName}_{(op == 38 ? "TextureSize" : "TexelSize")}")));
                    break;
                }

                case 42:                {
                    if (i + 15 > n) return null;
                    ushort nameIdx = BitConverter.ToUInt16(data, dataStart + i);
                    int textureIdx = BitConverter.ToInt32(data, dataStart + i + 7);
                    int vectorIdx = BitConverter.ToInt32(data, dataStart + i + 11);
                    i += 15;
                    string texName = ResolveTextureName(nameIdx, textureIdx, preshaderNames, textureParameters);
                    firstParamName ??= texName;
                    stack.Push(StackVal.Expr($"{texName}_RVTUniform_{vectorIdx}", "x" + ProgramQuote($"{texName}_RVTUniform_{vectorIdx}")));
                    break;
                }

                case 40:                case 41:                {
                    if (i + 22 > n) return null;
                    ushort nameIdx = BitConverter.ToUInt16(data, dataStart + i);
                    int textureIdx = BitConverter.ToInt32(data, dataStart + i + 18);
                    i += 22;
                    string texName = ResolveTextureName(nameIdx, textureIdx, preshaderNames, textureParameters);
                    firstParamName ??= texName;
                    stack.Push(StackVal.Expr($"{texName}_{(op == 40 ? "ExtTexCoordScaleRotation" : "ExtTexCoordOffset")}", "x" + ProgramQuote($"{texName}_{(op == 40 ? "ExtTexCoordScaleRotation" : "ExtTexCoordOffset")}")));
                    break;
                }

                case 4: case 5: case 6: case 7: case 8:
                case 9: case 10:
                case 18: case 19: case 20:
                case 49: case 51: case 52: case 53:
                {
                    if (stack.Count < 2) return null;
                    StackVal b = stack.Pop();
                    StackVal a = stack.Pop();
                    stack.Push(StackVal.Expr(FormatBinary(op, a, b), $"({ProgramOpToken(op) ?? "?"} {a.Program} {b.Program})"));
                    break;
                }

                case 11:
                {
                    if (stack.Count < 3) return null;
                    StackVal hi = stack.Pop();
                    StackVal lo = stack.Pop();
                    StackVal x = stack.Pop();
                    stack.Push(StackVal.Expr(FormatClamp(x, lo, hi), $"(clamp {x.Program} {lo.Program} {hi.Program})"));
                    break;
                }

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
                    stack.Push(StackVal.Expr($"{uname}_{x.Name}", $"({ProgramOpToken(op) ?? uname} {x.Program})"));
                    break;
                }

                default:
                    return null;
            }
        }

        if (stack.Count == 0) return null;
        StackVal top = stack.Peek();
        program = top.Program;

        string baseName = firstParamName ?? (firstExternalId.HasValue ? $"ext_{firstExternalId.Value}" : null!);
        string expr = top.Name;
        if (baseName == null)
        {
            return null;
        }

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

        public readonly string Program;

        private StackVal(string name, bool isParam, bool isConst, string? lit, string program)
        {
            Name = name; IsParam = isParam; IsConst = isConst; ConstLiteral = lit; Program = program;
        }

        public static StackVal Param(string n, string program) => new(n, true, false, null, program);
        public static StackVal Const(string lit, string program) => new(lit, false, true, lit, program);
        public static StackVal Expr(string n, string program) => new(n, false, false, null, program);
    }

    private static string? ProgramOpToken(byte op) => op switch
    {
        4 => "add", 5 => "sub", 6 => "mul", 7 => "div", 8 => "fmod",
        9 => "min", 10 => "max", 11 => "clamp",
        18 => "atan2", 19 => "dot", 20 => "cross", 37 => "append",
        49 => "lt", 51 => "gt", 52 => "le", 53 => "ge",
        12 => "sin", 13 => "cos", 14 => "tan", 15 => "asin", 16 => "acos", 17 => "atan",
        21 => "sqrt", 22 => "rcp", 23 => "length", 24 => "normalize", 25 => "saturate",
        26 => "abs", 27 => "floor", 28 => "ceil", 29 => "round", 30 => "trunc",
        31 => "sign", 32 => "frac", 33 => "frac", 34 => "log2", 35 => "log10",
        45 => "neg",
        _ => null,
    };

    private static int ParameterComponentCount(JsonElement parameter)
    {
        if (string.Equals(ReadString(parameter, "ParameterType"), "Scalar", StringComparison.Ordinal)) return 1;
        if (parameter.ValueKind == JsonValueKind.Object
            && parameter.TryGetProperty("Value", out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.Number) return 1;
            if (value.ValueKind == JsonValueKind.Object)
            {
                int last = 0;
                string[] names = { "R", "G", "B", "A" };
                for (int c = 0; c < 4; c++)
                {
                    if (value.TryGetProperty(names[c], out JsonElement comp) && comp.ValueKind == JsonValueKind.Number) last = c + 1;
                }
                if (last > 0) return last;
            }
        }
        return 4;
    }

    private static string ProgramQuote(string raw) => "\"" + raw.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string ConstProgram(byte[] data, int valueStart, byte ctype)
    {
        int components = ctype switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, _ => 0 };
        if (components == 0 || valueStart + (components * 4) > data.Length) return "c1:0";

        var parts = new string[components];
        for (int c = 0; c < components; c++)
        {
            parts[c] = BitConverter.ToSingle(data, valueStart + (c * 4)).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        return $"c{components}:{string.Join(",", parts)}";
    }

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

    private static float[]? TryEvaluatePreshaderNumeric(byte[] data, uint offset, uint size, JsonElement parameters)
        => TryEvaluatePreshaderStack(data, offset, size, parameters) is { Count: > 0 } s ? s[^1] : null;

    private static List<float[]>? TryEvaluatePreshaderStack(byte[] data, uint offset, uint size, JsonElement parameters)
    {
        int n = checked((int)size);
        int dataStart = checked((int)offset);
        if (dataStart < 0 || dataStart > data.Length) return null;
        if (dataStart + n > data.Length) n = data.Length - dataStart;
        if (n < 1) return null;

        Stack<int> widths = new();
        Stack<float[]> stack = new();
        int i = 0;
        while (i < n)
        {
            byte op = TranslateOpcode(data[dataStart + i]);
            i++;

            switch (op)
            {
                case 0: break;                case 1: stack.Push(new float[4]); widths.Push(4); break;
                case 2:                {
                    if (i >= n) return null;
                    byte ctype = data[dataStart + i];
                    int comps = ctype switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, _ => -1 };
                    if (comps < 0 || i + 1 + comps * 4 > n) return null;
                    float[] v = new float[4];
                    for (int c = 0; c < comps; c++) v[c] = BitConverter.ToSingle(data, dataStart + i + 1 + c * 4);
                    if (comps == 1) { v[1] = v[0]; v[2] = v[0]; v[3] = v[0]; }
                    stack.Push(v);
                    widths.Push(comps);
                    i += 1 + comps * 4;
                    break;
                }

                case 3:                {
                    if (i + 2 > n) return null;
                    ushort idx = BitConverter.ToUInt16(data, dataStart + i);
                    if (idx >= parameters.GetArrayLength()) return null;
                    float[]? pv = ReadParameterValue(parameters[idx]);
                    if (pv == null) return null;
                    stack.Push(pv);
                    widths.Push(ParameterComponentCount(parameters[idx]));
                    i += 2;
                    break;
                }

                case 36:                {
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
                    if (widths.Count > 0) widths.Pop();
                    widths.Push(numE > 0 ? numE : 1);
                    break;
                }

                case 37:                {
                    if (stack.Count < 2 || widths.Count < 2) return null;
                    float[] b = stack.Pop();
                    float[] a = stack.Pop();
                    int bw = widths.Pop();
                    int aw = widths.Pop();
                    float[] merged = new float[4];
                    int at = 0;
                    for (int c = 0; c < aw && at < 4; c++) merged[at++] = a[c];
                    for (int c = 0; c < bw && at < 4; c++) merged[at++] = b[c];
                    stack.Push(merged);
                    widths.Push(at);
                    break;
                }

                case 4: case 5: case 6: case 7: case 8: case 9: case 10:
                case 18: case 19: case 20:
                case 49: case 51: case 52: case 53:
                {
                    if (stack.Count < 2 || widths.Count < 2) return null;
                    float[] b = stack.Pop();
                    float[] a = stack.Pop();
                    int bw = widths.Pop();
                    int aw = widths.Pop();
                    float[]? r = ApplyBinary(op, a, b);
                    if (r == null) return null;
                    stack.Push(r);
                    widths.Push(op == 19 ? 1 : Math.Max(aw, bw));                    break;
                }

                case 11:                {
                    if (stack.Count < 3 || widths.Count < 3) return null;
                    float[] hi = stack.Pop();
                    float[] lo = stack.Pop();
                    float[] x = stack.Pop();
                    widths.Pop();
                    widths.Pop();
                    int xw = widths.Pop();
                    float[] r = new float[4];
                    for (int c = 0; c < 4; c++) r[c] = MathF.Min(MathF.Max(x[c], lo[c]), hi[c]);
                    stack.Push(r);
                    widths.Push(xw);
                    break;
                }

                case 12: case 13: case 14: case 15: case 16: case 17:
                case 21: case 22: case 23: case 24: case 25: case 26:
                case 27: case 28: case 29: case 30: case 31: case 32:
                case 33: case 34: case 35: case 45:
                {
                    if (stack.Count < 1 || widths.Count < 1) return null;
                    float[] x = stack.Pop();
                    int xw = widths.Pop();
                    float[]? r = ApplyUnary(op, x);
                    if (r == null) return null;
                    stack.Push(r);
                    widths.Push(op == 23 ? 1 : xw);                    break;
                }

                case 38:
                case 39:
                case 42:
                {
                    int operand = op == 42 ? 15 : 11;
                    if (i + operand > n) return null;
                    i += operand;
                    stack.Push(new float[4]);
                    widths.Push(op == 42 ? 4 : 2);
                    break;
                }

                case 40:
                case 41:
                {
                    if (i + 22 > n) return null;
                    i += 22;
                    stack.Push(new float[4]);
                    widths.Push(4);
                    break;
                }

                default:
                    if (Environment.GetEnvironmentVariable("RURI_PRESHADER_DEBUG") == "1")
                    {
                        Console.WriteLine($"[preshader-eval] 未实现 opcode 0x{op:X2}({op}) raw=0x{data[dataStart + i - 1]:X2} —— 该段放弃求值");
                    }
                    return null;
            }
        }

        if (stack.Count == 0) return null;
        var bottomToTop = new List<float[]>(stack);        bottomToTop.Reverse();
        return bottomToTop;
    }

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
            case 19:            {
                float d = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
                return new[] { d, d, d, d };
            }
            case 20:                return new[]
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
            case 23:            {
                float l = MathF.Sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]);
                return new[] { l, l, l, l };
            }
            case 24:            {
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
        if (v == MathF.Truncate(v) && MathF.Abs(v) < 1e7f)
        {
            long iv = (long)v;
            return iv < 0 ? $"neg{-iv}" : iv.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        string raw = v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
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
        if (sb.Length > 0 && sb[0] >= '0' && sb[0] <= '9')
        {
            sb.Insert(0, '_');
        }
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
        const int MaxLen = 512;
        if (s.Length <= MaxLen) return s;
        return s.Substring(0, MaxLen - 4) + "_etc";
    }

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
            byte op = TranslateOpcode(data[dataStart + i]);
            int operandBytes;

            if (op == 3)            {
                if (i + 1 + 2 > n) return null;
                ushort idx = BitConverter.ToUInt16(data, dataStart + i + 1);
                if (idx >= parameters.GetArrayLength()) return null;
                if (singleIdx.HasValue && singleIdx.Value != idx) return null;
                singleIdx = idx;
                operandBytes = 2;
            }
            else if (op == 2)            {
                if (i + 1 >= n) return null;
                int valueBytes = data[dataStart + i + 1] switch
                {
                    1 => 4,                    2 => 8,                    3 => 12,                    4 => 16,                    _ => -1,                };
                if (valueBytes < 0) return null;
                operandBytes = 1 + valueBytes;
            }
            else if (op == 36)            {
                operandBytes = 5;
            }
            else if (op == 255)
            {
                return null;
            }
            else
            {
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
