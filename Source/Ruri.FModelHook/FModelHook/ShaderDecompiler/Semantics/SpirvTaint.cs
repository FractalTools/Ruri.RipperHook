using Ruri.ShaderTools.Spirv;
using Ruri.ShaderTools.Spirv.Analysis;

namespace Ruri.FModelHook.ShaderDecompiler.Semantics;

/// <summary>
/// Which texture channels and which uniform-buffer lanes reach which fragment outputs, and
/// the discard test, in one SPIR-V pixel shader: a backward walk from every store into an
/// output location over the value graph, per vector component, down to the image sampling
/// instructions and the descriptor binding each one samples, and down to the loads out of
/// uniform blocks and the byte each one reads. Components are tracked exactly through
/// extraction, construction, shuffling and insertion; every other instruction passes the
/// union of its operands on, a function-local variable carries the union of everything stored
/// to it, and a call carries the union of its callee's returns and its arguments. Cycles
/// through phi nodes settle by repeating the walk until no output set grows.
/// </summary>
internal sealed class SpirvTaint
{
    public enum SourceKind
    {
        Texture,
        Uniform,
    }

    /// <summary>
    /// One channel of one sampled texture binding, or one 32-bit lane of one uniform block:
    /// <see cref="Offset"/> is the byte offset of the 16-byte register the lane sits in and
    /// <see cref="Channel"/> the lane within it, the way a constant buffer is laid out.
    /// </summary>
    public readonly record struct Source(SourceKind Kind, int Binding, int Channel, int Offset)
    {
        public static Source Texture(int binding, int channel) => new(SourceKind.Texture, binding, channel, -1);

        public static Source Uniform(int binding, int byteOffset) => new(SourceKind.Uniform, binding, byteOffset % RegisterBytes / LaneBytes, byteOffset - byteOffset % RegisterBytes);

        public bool IsTexture => Kind == SourceKind.Texture;

        public bool IsUniform => Kind == SourceKind.Uniform;

        /// <summary>The byte offset of the lane itself, for a uniform source.</summary>
        public int LaneOffset => Offset + Channel * LaneBytes;
    }

    private const uint UndefinedComponent = 0xFFFFFFFF;
    private const int SampleWidth = 4;
    private const int Rounds = 4;
    private const int RegisterBytes = 16;
    private const int LaneBytes = 4;
    private static readonly HashSet<Source> Nothing = new();

    private readonly SpirvModule module;
    private readonly ResultIdTable definitions;
    private readonly ModuleShape shape;
    private readonly Dictionary<uint, uint> resultTypes = new();
    private readonly Dictionary<uint, int> outputLocations = new();
    private readonly Dictionary<(uint Struct, uint Member), int> memberOffsets = new();
    private readonly Dictionary<uint, int> scalarBytes = new();
    private readonly Dictionary<uint, List<(uint Value, int? Component)>> variableStores = new();
    private readonly Dictionary<uint, List<uint>> functionReturns = new();
    private readonly List<(uint Variable, int? Component, uint Value)> outputStores = new();
    private readonly List<(uint Condition, uint TrueLabel, uint FalseLabel)> conditionalBranches = new();
    private readonly HashSet<uint> killBlocks = new();
    private readonly Dictionary<uint, HashSet<Source>[]> memo = new();
    private Dictionary<uint, HashSet<Source>[]> previous = new();
    private readonly HashSet<uint> visiting = new();
    private readonly Dictionary<int, HashSet<Source>[]> outputs = new();
    private readonly HashSet<Source> discard = new();

    private SpirvTaint(SpirvModule module)
    {
        this.module = module;
        definitions = ResultIdTable.Build(module);
        shape = ModuleShape.Build(module);
    }

    /// <summary>Per output location, per component, the sources that reach it.</summary>
    public IReadOnlyDictionary<int, HashSet<Source>[]> Outputs => outputs;

    /// <summary>The sources that decide whether the fragment is discarded.</summary>
    public IReadOnlySet<Source> Discard => discard;

    public static SpirvTaint Analyze(byte[] spirv)
    {
        SpirvTaint taint = new(SpirvModule.Parse(spirv));
        taint.Index();
        taint.Settle();
        return taint;
    }

    private void Index()
    {
        Dictionary<uint, int> locations = new();
        uint currentFunction = 0;
        uint currentBlock = 0;
        foreach (SpirvInstruction instruction in module.Instructions)
        {
            Span<uint> words = instruction.Words;
            if (SpvInstructionTraits.HasResultType(instruction.OpCode) && words.Length >= 3)
            {
                resultTypes[words[2]] = words[1];
            }
            switch (instruction.OpCode)
            {
                case SpvOpCode.OpDecorate when words.Length >= 4 && words[2] == Decoration.Location:
                    locations[words[1]] = (int)words[3];
                    break;
                case SpvOpCode.OpMemberDecorate when words.Length >= 5 && words[3] == Decoration.Offset:
                    memberOffsets[(words[1], words[2])] = (int)words[4];
                    break;
                case SpvOpCode.OpTypeFloat when words.Length >= 3:
                case SpvOpCode.OpTypeInt when words.Length >= 3:
                    scalarBytes[words[1]] = (int)(words[2] / 8);
                    break;
                case SpvOpCode.OpTypeBool when words.Length >= 2:
                    scalarBytes[words[1]] = LaneBytes;
                    break;
                case SpvOpCode.OpVariable when words.Length >= 4 && words[3] == StorageClass.Output && locations.TryGetValue(words[2], out int location):
                    outputLocations[words[2]] = location;
                    break;
                case SpvOpCode.OpFunction when words.Length >= 3:
                    currentFunction = words[2];
                    break;
                case SpvOpCode.OpFunctionEnd:
                    currentFunction = 0;
                    break;
                case SpvOpCode.OpLabel when words.Length >= 2:
                    currentBlock = words[1];
                    break;
                case SpvOpCode.OpReturnValue when words.Length >= 2 && currentFunction != 0:
                    Returns(currentFunction).Add(words[1]);
                    break;
                case SpvOpCode.OpKill:
                case SpvOpCode.OpTerminateInvocation:
                case SpvOpCode.OpDemoteToHelperInvocation:
                    killBlocks.Add(currentBlock);
                    break;
                case SpvOpCode.OpBranchConditional when words.Length >= 4:
                    conditionalBranches.Add((words[1], words[2], words[3]));
                    break;
                case SpvOpCode.OpStore when words.Length >= 3:
                    Store(words[1], words[2]);
                    break;
            }
        }
    }

    private List<uint> Returns(uint function)
    {
        if (!functionReturns.TryGetValue(function, out List<uint>? returns))
        {
            functionReturns[function] = returns = new List<uint>();
        }
        return returns;
    }

    private void Store(uint pointer, uint value)
    {
        (uint variable, int? component) = ResolvePointer(pointer);
        if (variable == 0)
        {
            return;
        }
        if (outputLocations.ContainsKey(variable))
        {
            outputStores.Add((variable, component, value));
            return;
        }
        if (!variableStores.TryGetValue(variable, out List<(uint, int?)>? stores))
        {
            variableStores[variable] = stores = new List<(uint, int?)>();
        }
        stores.Add((value, component));
    }

    /// <summary>The variable a pointer addresses and, for a chain ending in a constant index into a vector, that component.</summary>
    private (uint Variable, int? Component) ResolvePointer(uint pointer)
    {
        SpirvInstruction? definition = definitions.DefinitionOf(pointer);
        if (definition is null)
        {
            return (0, null);
        }
        Span<uint> words = definition.Words;
        switch (definition.OpCode)
        {
            case SpvOpCode.OpVariable:
                return (words[2], null);
            case SpvOpCode.OpAccessChain:
            case SpvOpCode.OpInBoundsAccessChain:
            {
                (uint variable, int? _) = ResolvePointer(words[3]);
                int? component = null;
                if (words.Length >= 5 && shape.Constants.TryGetValue(words[^1], out uint index))
                {
                    component = (int)index;
                }
                return (variable, component);
            }
            default:
                return (0, null);
        }
    }

    private void Settle()
    {
        for (int round = 0; round < Rounds; round++)
        {
            previous = new Dictionary<uint, HashSet<Source>[]>(memo);
            memo.Clear();
            visiting.Clear();
            int before = Size();
            Collect();
            if (round > 0 && Size() == before)
            {
                break;
            }
        }
    }

    private int Size()
    {
        int size = discard.Count;
        foreach (HashSet<Source>[] components in outputs.Values)
        {
            foreach (HashSet<Source> component in components)
            {
                size += component.Count;
            }
        }
        return size;
    }

    private void Collect()
    {
        foreach ((uint variable, int? component, uint value) in outputStores)
        {
            int location = outputLocations[variable];
            HashSet<Source>[] flow = Taint(value);
            HashSet<Source>[] target = OutputComponents(location, component is { } index ? index + 1 : flow.Length);
            if (component is { } single)
            {
                target[single].UnionWith(All(flow));
            }
            else
            {
                for (int i = 0; i < flow.Length && i < target.Length; i++)
                {
                    target[i].UnionWith(flow[i]);
                }
            }
        }
        foreach ((uint condition, uint trueLabel, uint falseLabel) in conditionalBranches)
        {
            if (killBlocks.Contains(trueLabel) || killBlocks.Contains(falseLabel))
            {
                discard.UnionWith(All(Taint(condition)));
            }
        }
    }

    private HashSet<Source>[] OutputComponents(int location, int atLeast)
    {
        if (!outputs.TryGetValue(location, out HashSet<Source>[]? components))
        {
            outputs[location] = components = Fresh(Math.Max(atLeast, SampleWidth));
        }
        else if (components.Length < atLeast)
        {
            HashSet<Source>[] grown = Fresh(atLeast);
            Array.Copy(components, grown, components.Length);
            outputs[location] = components = grown;
        }
        return components;
    }

    private HashSet<Source>[] Taint(uint id)
    {
        if (memo.TryGetValue(id, out HashSet<Source>[]? known))
        {
            return known;
        }
        if (!visiting.Add(id))
        {
            return previous.TryGetValue(id, out HashSet<Source>[]? earlier) ? earlier : Empty(Width(id));
        }
        HashSet<Source>[] result = Compute(id);
        visiting.Remove(id);
        memo[id] = result;
        return result;
    }

    private HashSet<Source>[] Compute(uint id)
    {
        SpirvInstruction? definition = definitions.DefinitionOf(id);
        if (definition is null)
        {
            return Empty(1);
        }
        Span<uint> words = definition.Words;
        int width = Width(id);
        switch (definition.OpCode)
        {
            case SpvOpCode.OpImageSampleImplicitLod:
            case SpvOpCode.OpImageSampleExplicitLod:
            case SpvOpCode.OpImageSampleDrefImplicitLod:
            case SpvOpCode.OpImageSampleDrefExplicitLod:
            case SpvOpCode.OpImageSampleProjImplicitLod:
            case SpvOpCode.OpImageSampleProjExplicitLod:
            case SpvOpCode.OpImageSampleProjDrefImplicitLod:
            case SpvOpCode.OpImageSampleProjDrefExplicitLod:
                return Sampled(ImageBinding(words[3]), width);
            case SpvOpCode.OpImageFetch:
            case SpvOpCode.OpImageGather:
            case SpvOpCode.OpImageDrefGather:
            case SpvOpCode.OpImageRead:
                return Sampled(ImageBinding(words[3]), width);
            case SpvOpCode.OpCompositeExtract:
            {
                HashSet<Source>[] composite = Taint(words[3]);
                int index = words.Length >= 5 ? (int)words[4] : 0;
                return index < composite.Length ? [composite[index]] : [All(composite)];
            }
            case SpvOpCode.OpCompositeConstruct:
            {
                List<HashSet<Source>> parts = new();
                for (int i = 3; i < words.Length; i++)
                {
                    parts.AddRange(Taint(words[i]));
                }
                return parts.Count == 0 ? Empty(width) : parts.ToArray();
            }
            case SpvOpCode.OpVectorShuffle:
            {
                HashSet<Source>[] first = Taint(words[3]);
                HashSet<Source>[] second = Taint(words[4]);
                HashSet<Source>[] shuffled = Fresh(Math.Max(1, words.Length - 5));
                for (int i = 5; i < words.Length; i++)
                {
                    uint component = words[i];
                    if (component == UndefinedComponent)
                    {
                        continue;
                    }
                    if (component < first.Length)
                    {
                        shuffled[i - 5].UnionWith(first[component]);
                    }
                    else if (component - first.Length < second.Length)
                    {
                        shuffled[i - 5].UnionWith(second[component - first.Length]);
                    }
                }
                return shuffled;
            }
            case SpvOpCode.OpCompositeInsert:
            {
                HashSet<Source>[] composite = Copy(Taint(words[4]));
                int index = words.Length >= 6 ? (int)words[5] : 0;
                if (index < composite.Length)
                {
                    composite[index].UnionWith(All(Taint(words[3])));
                }
                return composite;
            }
            case SpvOpCode.OpVectorExtractDynamic:
                return [All(Taint(words[3]))];
            case SpvOpCode.OpPhi:
            {
                List<HashSet<Source>[]> incoming = new();
                for (int i = 3; i + 1 < words.Length; i += 2)
                {
                    incoming.Add(Taint(words[i]));
                }
                return Combine(width, incoming);
            }
            case SpvOpCode.OpSelect:
                return Combine(width, [Taint(words[3]), Taint(words[4]), Taint(words[5])]);
            case SpvOpCode.OpLoad:
                return Loaded(words[3], width);
            case SpvOpCode.OpFunctionCall:
            {
                List<HashSet<Source>[]> flows = new();
                if (functionReturns.TryGetValue(words[3], out List<uint>? returns))
                {
                    foreach (uint returned in returns)
                    {
                        flows.Add(Taint(returned));
                    }
                }
                for (int i = 4; i < words.Length; i++)
                {
                    flows.Add(Taint(words[i]));
                }
                return Combine(width, flows);
            }
            case SpvOpCode.OpExtInst:
                return Combine(width, Operands(words, 5));
            case SpvOpCode.OpConstant:
            case SpvOpCode.OpVariable:
            case SpvOpCode.OpAccessChain:
            case SpvOpCode.OpInBoundsAccessChain:
            case SpvOpCode.OpSampledImage:
            case SpvOpCode.OpImage:
            case SpvOpCode.OpFunctionParameter:
                return Empty(width);
            default:
                return SpvInstructionTraits.HasResultType(definition.OpCode) ? Combine(width, Operands(words, 3)) : Empty(width);
        }
    }

    private List<HashSet<Source>[]> Operands(Span<uint> words, int start)
    {
        List<HashSet<Source>[]> flows = new();
        for (int i = start; i < words.Length; i++)
        {
            if (definitions.DefinitionOf(words[i]) is not null)
            {
                flows.Add(Taint(words[i]));
            }
        }
        return flows;
    }

    /// <summary>A load out of a uniform block is the lanes it reads; out of a function-local variable, the union of what was stored there.</summary>
    private HashSet<Source>[] Loaded(uint pointer, int width)
    {
        (uint variable, int? component) = ResolvePointer(pointer);
        if (variable == 0)
        {
            return Empty(width);
        }
        if (UniformBinding(variable) is { } binding)
        {
            return UniformLanes(pointer, binding, width);
        }
        if (!variableStores.TryGetValue(variable, out List<(uint Value, int? Component)>? stores))
        {
            return Empty(width);
        }
        HashSet<Source>[] result = Fresh(width);
        foreach ((uint value, int? stored) in stores)
        {
            HashSet<Source>[] flow = Taint(value);
            if (component is { } wanted)
            {
                if (stored is null && wanted < flow.Length)
                {
                    result[0].UnionWith(flow[wanted]);
                }
                else if (stored == wanted)
                {
                    result[0].UnionWith(All(flow));
                }
            }
            else if (stored is { } index)
            {
                if (index < result.Length)
                {
                    result[index].UnionWith(All(flow));
                }
            }
            else
            {
                for (int i = 0; i < result.Length; i++)
                {
                    result[i].UnionWith(i < flow.Length ? flow[i] : Nothing);
                }
            }
        }
        return result;
    }

    /// <summary>The binding of a variable that is a uniform block, else null.</summary>
    private int? UniformBinding(uint variable)
    {
        if (!shape.VariablePointerTypes.TryGetValue(variable, out uint pointerType)
            || !shape.PointerTypes.TryGetValue(pointerType, out (uint StorageClass, uint TypeId) pointee)
            || pointee.StorageClass != StorageClass.Uniform
            || !shape.SetBindingById.TryGetValue(variable, out (int? Set, int? Binding) binding))
        {
            return null;
        }
        return binding.Binding;
    }

    /// <summary>
    /// The lanes a load of <paramref name="width"/> components reads, walking the access chain
    /// with the block's own layout: member offsets, array strides, vector components. A chain
    /// with an index the module does not state as a constant reads no lane anyone can name.
    /// </summary>
    private HashSet<Source>[] UniformLanes(uint pointer, int binding, int width)
    {
        HashSet<Source>[] result = Fresh(Math.Max(width, 1));
        if (UniformOffset(pointer) is not (int offset, uint type))
        {
            return result;
        }
        int laneBytes = ComponentBytes(type);
        for (int component = 0; component < result.Length; component++)
        {
            result[component].Add(Source.Uniform(binding, offset + component * laneBytes));
        }
        return result;
    }

    private (int Offset, uint Type)? UniformOffset(uint pointer)
    {
        SpirvInstruction? definition = definitions.DefinitionOf(pointer);
        if (definition is null)
        {
            return null;
        }
        Span<uint> words = definition.Words;
        switch (definition.OpCode)
        {
            case SpvOpCode.OpVariable:
                return shape.VariablePointerTypes.TryGetValue(words[2], out uint pointerType) && shape.PointerTypes.TryGetValue(pointerType, out (uint StorageClass, uint TypeId) pointee)
                    ? (0, pointee.TypeId)
                    : null;
            case SpvOpCode.OpAccessChain:
            case SpvOpCode.OpInBoundsAccessChain:
            {
                if (UniformOffset(words[3]) is not (int offset, uint type))
                {
                    return null;
                }
                for (int i = 4; i < words.Length; i++)
                {
                    if (!shape.Constants.TryGetValue(words[i], out uint index))
                    {
                        return null;
                    }
                    if (shape.StructMembers.TryGetValue(type, out uint[]? members))
                    {
                        if (index >= members.Length || !memberOffsets.TryGetValue((type, index), out int memberOffset))
                        {
                            return null;
                        }
                        offset += memberOffset;
                        type = members[index];
                    }
                    else if (shape.ArrayTypes.TryGetValue(type, out (uint ElementTypeId, uint LengthId) array))
                    {
                        if (!shape.ArrayStrides.TryGetValue(type, out uint stride))
                        {
                            return null;
                        }
                        offset += (int)(index * stride);
                        type = array.ElementTypeId;
                    }
                    else if (shape.VectorShapes.TryGetValue(type, out (uint ComponentTypeId, uint ComponentCount) vector))
                    {
                        offset += (int)index * ComponentBytes(vector.ComponentTypeId);
                        type = vector.ComponentTypeId;
                    }
                    else
                    {
                        return null;
                    }
                }
                return (offset, type);
            }
            default:
                return null;
        }
    }

    /// <summary>The bytes one component of a type takes: the scalar's own width, a vector's component's, else a lane.</summary>
    private int ComponentBytes(uint type)
    {
        if (scalarBytes.TryGetValue(type, out int bytes))
        {
            return bytes;
        }
        if (shape.VectorShapes.TryGetValue(type, out (uint ComponentTypeId, uint ComponentCount) vector) && scalarBytes.TryGetValue(vector.ComponentTypeId, out bytes))
        {
            return bytes;
        }
        return LaneBytes;
    }

    /// <summary>The descriptor binding behind a sampled image or image operand, through the combine and the load that carried it.</summary>
    private int ImageBinding(uint operand)
    {
        uint cursor = operand;
        for (int hops = 0; hops < 8; hops++)
        {
            SpirvInstruction? definition = definitions.DefinitionOf(cursor);
            if (definition is null)
            {
                return -1;
            }
            Span<uint> words = definition.Words;
            switch (definition.OpCode)
            {
                case SpvOpCode.OpSampledImage:
                case SpvOpCode.OpImage:
                    cursor = words[3];
                    continue;
                case SpvOpCode.OpLoad:
                    cursor = words[3];
                    continue;
                case SpvOpCode.OpAccessChain:
                case SpvOpCode.OpInBoundsAccessChain:
                    cursor = words[3];
                    continue;
                case SpvOpCode.OpVariable:
                    return shape.SetBindingById.TryGetValue(words[2], out (int? Set, int? Binding) binding) && binding.Binding is { } index ? index : -1;
                default:
                    return -1;
            }
        }
        return -1;
    }

    private static HashSet<Source>[] Sampled(int binding, int width)
    {
        HashSet<Source>[] result = Fresh(Math.Max(width, 1));
        if (binding < 0)
        {
            return result;
        }
        if (result.Length == 1)
        {
            for (int channel = 0; channel < SampleWidth; channel++)
            {
                result[0].Add(Source.Texture(binding, channel));
            }
            return result;
        }
        for (int channel = 0; channel < result.Length && channel < SampleWidth; channel++)
        {
            result[channel].Add(Source.Texture(binding, channel));
        }
        return result;
    }

    private int Width(uint id)
    {
        if (!resultTypes.TryGetValue(id, out uint type))
        {
            return 1;
        }
        if (shape.TryGetVectorShape(type, out _, out uint count))
        {
            return (int)count;
        }
        if (shape.StructMembers.TryGetValue(type, out uint[]? members))
        {
            return Math.Max(1, members.Length);
        }
        return 1;
    }

    private static HashSet<Source>[] Combine(int width, IReadOnlyList<HashSet<Source>[]> inputs)
    {
        HashSet<Source>[] result = Fresh(Math.Max(width, 1));
        foreach (HashSet<Source>[] input in inputs)
        {
            if (input.Length == result.Length)
            {
                for (int i = 0; i < result.Length; i++)
                {
                    result[i].UnionWith(input[i]);
                }
            }
            else
            {
                HashSet<Source> everything = All(input);
                for (int i = 0; i < result.Length; i++)
                {
                    result[i].UnionWith(everything);
                }
            }
        }
        return result;
    }

    private static HashSet<Source> All(HashSet<Source>[] components)
    {
        HashSet<Source> all = new();
        foreach (HashSet<Source> component in components)
        {
            all.UnionWith(component);
        }
        return all;
    }

    private static HashSet<Source>[] Copy(HashSet<Source>[] components)
    {
        HashSet<Source>[] copy = new HashSet<Source>[components.Length];
        for (int i = 0; i < components.Length; i++)
        {
            copy[i] = new HashSet<Source>(components[i]);
        }
        return copy;
    }

    private static HashSet<Source>[] Fresh(int width)
    {
        HashSet<Source>[] fresh = new HashSet<Source>[width];
        for (int i = 0; i < width; i++)
        {
            fresh[i] = new HashSet<Source>();
        }
        return fresh;
    }

    private static HashSet<Source>[] Empty(int width) => Fresh(Math.Max(width, 1));
}
