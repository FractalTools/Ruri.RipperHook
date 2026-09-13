using CUE4Parse.UE4.Assets.Exports.Material;

namespace Ruri.FModelHook.ShaderDecompiler;

/// <summary>
/// A material's constant buffer as ONE statement, whichever way its engine wrote it down.
///
/// The engine changed the shape twice over. A UE5 cook states one numeric-parameter table, a
/// list of preshader programs, and a separate field table saying which buffer offset each
/// program's results land at. A UE4 cook states no field table at all: it keeps vector and
/// scalar expressions in two parallel lists whose POSITION is the offset -- vector i at
/// register i, scalar i packed four to a register after them -- and two parameter tables, one
/// per kind, which their own programs index separately.
///
/// Both say exactly the same thing, so it is said once here: a list of programs, each with the
/// offsets it fills and the parameters it reads. Everything downstream -- the evaluator, the
/// member names, the shaderlab Properties -- speaks only this, and neither knows an engine
/// version. Reading only the UE5 shape is what left every pre-UE5 title with an unnamed
/// constant buffer and not one numeric knob in its Properties.
/// </summary>
internal sealed class MaterialExpressions
{
    /// <summary>What the engine calls the bucket whose uniforms sit between the page table and the preshaders.</summary>
    private const string VirtualTextureKind = "Virtual";

    private const int RegisterBytes = 16;
    private const int ComponentBytes = 4;
    private const int ScalarsPerRegister = 4;

    /// <summary>One member a preshader program fills: where it lands and how wide it is.</summary>
    internal readonly record struct Field(int ByteOffset, string Type);

    /// <summary>One preshader program, the members it fills, and the parameter table its opcodes index.</summary>
    internal sealed record Program(uint OpcodeOffset, uint OpcodeSize, IReadOnlyList<Field> Fields, NumericParameter[] Parameters);

    private MaterialExpressions(byte[] opcodes, IReadOnlyList<Program> programs, NumericParameter[] parameters,
        string[] names, IReadOnlyList<IReadOnlyList<string>> textureNames,
        int constantBufferSize, int numericRegionEnd, int virtualPageTableBytes, int virtualUniformBytes)
    {
        Opcodes = opcodes;
        Programs = programs;
        Parameters = parameters;
        Names = names;
        TextureNames = textureNames;
        ConstantBufferSize = constantBufferSize;
        NumericRegionEnd = numericRegionEnd;
        VirtualPageTableBytes = virtualPageTableBytes;
        VirtualUniformBytes = virtualUniformBytes;
    }

    public byte[] Opcodes { get; }

    public IReadOnlyList<Program> Programs { get; }

    /// <summary>Every numeric parameter the material declares, whichever table it came from.</summary>
    public NumericParameter[] Parameters { get; }

    public string[] Names { get; }

    public IReadOnlyList<IReadOnlyList<string>> TextureNames { get; }

    public int ConstantBufferSize { get; }

    /// <summary>Where the buffer stops being numbers and starts being resource handles.</summary>
    public int NumericRegionEnd { get; }

    public int VirtualPageTableBytes { get; }

    public int VirtualUniformBytes { get; }

    public static MaterialExpressions? Of(FUniformExpressionSet? expressionSet)
    {
        byte[]? opcodes = expressionSet?.UniformPreshaderData?.Data;
        if (expressionSet is null || opcodes is null)
        {
            return null;
        }

        FRHIUniformBufferLayoutInitializer? layout = expressionSet.UniformBufferLayoutInitializer;
        int constantBufferSize = layout is null ? 0 : (int)layout.ConstantBufferSize;
        FRHIUniformBufferResource[]? resources = layout?.Resources;
        int numericRegionEnd = resources is { Length: > 0 } ? (int)resources[0].MemberOffset : constantBufferSize;

        string[] names = Array.ConvertAll(expressionSet.UniformPreshaderData.Names ?? [], static name => name.Text ?? string.Empty);
        IReadOnlyList<IReadOnlyList<string>> textureNames = PreshaderInputs.TextureBuckets(expressionSet.UniformTextureParameters);

        return expressionSet.UniformPreshaders is { Length: > 0 }
            ? Stated(expressionSet, opcodes, names, textureNames, constantBufferSize, numericRegionEnd)
            : Positional(expressionSet, opcodes, names, textureNames, constantBufferSize, numericRegionEnd);
    }

    /// <summary>
    /// The shape that says where each result lands: the programs point into a field table, and
    /// the fields are offsets inside a preshader buffer that sits after the virtual-texture
    /// uniforms.
    /// </summary>
    private static MaterialExpressions Stated(FUniformExpressionSet expressionSet, byte[] opcodes,
        string[] names, IReadOnlyList<IReadOnlyList<string>> textureNames, int constantBufferSize, int numericRegionEnd)
    {
        NumericParameter[] parameters = PreshaderInputs.Parameters(expressionSet.UniformNumericParameters);
        FMaterialUniformPreshaderField[] fields = expressionSet.UniformPreshaderFields ?? [];

        int preshaderBufferBytes = Math.Max(0, (int)expressionSet.UniformPreshaderBufferSize) * RegisterBytes;
        FMaterialTextureParameterInfo[][]? buckets = expressionSet.UniformTextureParameters;
        int virtualBucket = MaterialTextureOrder.BucketOf(VirtualTextureKind);
        int virtualCount = virtualBucket >= 0 && buckets is not null && virtualBucket < buckets.Length
            ? buckets[virtualBucket]?.Length ?? 0
            : 0;
        int virtualUniformBytes = virtualCount * RegisterBytes;
        int virtualPageTableBytes = Math.Max(0, numericRegionEnd - preshaderBufferBytes - virtualUniformBytes);
        int preshaderBufferStart = virtualPageTableBytes + virtualUniformBytes;

        List<Program> programs = new(expressionSet.UniformPreshaders!.Length);
        foreach (FMaterialUniformPreshaderHeader header in expressionSet.UniformPreshaders)
        {
            uint fieldIndex = header is FMaterialUniformPreshaderHeader_5_1 fielded ? fielded.FieldIndex : 0;
            uint fieldCount = header is FMaterialUniformPreshaderHeader_5_1 counted ? counted.NumFields : 0;
            if (fieldCount < 1 || fieldIndex + fieldCount > (uint)fields.Length)
            {
                continue;
            }
            List<Field> filled = new((int)fieldCount);
            for (uint slot = 0; slot < fieldCount; slot++)
            {
                FMaterialUniformPreshaderField field = fields[checked((int)(fieldIndex + slot))];
                filled.Add(new Field(preshaderBufferStart + checked((int)field.BufferOffset * ComponentBytes), field.Type.ToString()));
            }
            programs.Add(new Program(header.OpcodeOffset, header.OpcodeSize, filled, parameters));
        }
        return new MaterialExpressions(opcodes, programs, parameters, names, textureNames,
            constantBufferSize, numericRegionEnd, virtualPageTableBytes, virtualUniformBytes);
    }

    /// <summary>
    /// The shape whose POSITION is the offset: every vector expression takes a register of its
    /// own from the front of the buffer, every scalar expression a lane of the registers that
    /// follow them, and each kind's programs index its own parameter table.
    /// </summary>
    private static MaterialExpressions Positional(FUniformExpressionSet expressionSet, byte[] opcodes,
        string[] names, IReadOnlyList<IReadOnlyList<string>> textureNames, int constantBufferSize, int numericRegionEnd)
    {
        NumericParameter[] vectorParameters = Named(expressionSet.UniformVectorParameters);
        NumericParameter[] scalarParameters = Named(expressionSet.UniformScalarParameters);
        FMaterialUniformPreshaderHeader[] vectors = expressionSet.UniformVectorPreshaders ?? [];
        FMaterialUniformPreshaderHeader[] scalars = expressionSet.UniformScalarPreshaders ?? [];

        List<Program> programs = new(vectors.Length + scalars.Length);
        for (int slot = 0; slot < vectors.Length; slot++)
        {
            programs.Add(new Program(vectors[slot].OpcodeOffset, vectors[slot].OpcodeSize,
                [new Field(slot * RegisterBytes, "Float4")], vectorParameters));
        }
        int scalarBase = vectors.Length * RegisterBytes;
        for (int slot = 0; slot < scalars.Length; slot++)
        {
            int offset = scalarBase + slot / ScalarsPerRegister * RegisterBytes + slot % ScalarsPerRegister * ComponentBytes;
            programs.Add(new Program(scalars[slot].OpcodeOffset, scalars[slot].OpcodeSize,
                [new Field(offset, "Float1")], scalarParameters));
        }

        NumericParameter[] parameters = new NumericParameter[vectorParameters.Length + scalarParameters.Length];
        vectorParameters.CopyTo(parameters, 0);
        scalarParameters.CopyTo(parameters, vectorParameters.Length);
        return new MaterialExpressions(opcodes, programs, parameters, names, textureNames,
            constantBufferSize, numericRegionEnd, 0, 0);
    }

    private static NumericParameter[] Named(FMaterialVectorParameterInfo[]? parameters)
    {
        if (parameters is null)
        {
            return [];
        }
        NumericParameter[] result = new NumericParameter[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            FMaterialVectorParameterInfo parameter = parameters[i];
            result[i] = new NumericParameter(PreshaderInputs.NameOf(parameter) ?? string.Empty,
                [parameter.DefaultValue.R, parameter.DefaultValue.G, parameter.DefaultValue.B, parameter.DefaultValue.A], 4,
                EMaterialParameterType.Vector);
        }
        return result;
    }

    private static NumericParameter[] Named(FMaterialScalarParameterInfo[]? parameters)
    {
        if (parameters is null)
        {
            return [];
        }
        NumericParameter[] result = new NumericParameter[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            float value = parameters[i].DefaultValue;
            result[i] = new NumericParameter(PreshaderInputs.NameOf(parameters[i]) ?? string.Empty,
                [value, value, value, value], 1, EMaterialParameterType.Scalar);
        }
        return result;
    }
}
