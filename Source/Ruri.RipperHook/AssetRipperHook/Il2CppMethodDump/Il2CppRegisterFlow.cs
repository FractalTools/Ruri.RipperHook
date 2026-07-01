extern alias icedreal;
using System;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.PE;
using icedreal::Iced.Intel;

namespace Ruri.RipperHook.AR;

/// <summary>Canonical 0..15 index for a 64-bit general-purpose register (sub-registers fold to their full parent).</summary>
internal static class RegisterFlowUtil
{
    public static int GpIndex(Register register)
    {
        Register full = register.GetFullRegister();
        return full switch
        {
            Register.RAX => 0, Register.RCX => 1, Register.RDX => 2, Register.RBX => 3,
            Register.RSP => 4, Register.RBP => 5, Register.RSI => 6, Register.RDI => 7,
            Register.R8 => 8, Register.R9 => 9, Register.R10 => 10, Register.R11 => 11,
            Register.R12 => 12, Register.R13 => 13, Register.R14 => 14, Register.R15 => 15,
            _ => -1,
        };
    }
}

internal enum TrackedKind : byte { Unknown, ManagedRef, TypeInfo, StaticBase }

/// <summary>
/// Abstract value held in a register: a managed object pointer of a known type, a runtime <c>Il2CppClass*</c>
/// (<see cref="TrackedKind.TypeInfo"/>), or a type's static-fields base (<see cref="TrackedKind.StaticBase"/>).
/// <see cref="Alias"/> is the human-readable access path (<c>this</c>, <c>this.weapon</c>, an arg name) used for
/// comments; it is deliberately excluded from equality so the dataflow meet converges.
/// </summary>
internal readonly struct TrackedValue : IEquatable<TrackedValue>
{
    public readonly TrackedKind Kind;
    public readonly TypeAnalysisContext Type;
    public readonly string Alias;

    private TrackedValue(TrackedKind kind, TypeAnalysisContext type, string alias)
    {
        Kind = kind;
        Type = type;
        Alias = alias;
    }

    public static readonly TrackedValue Unknown = default;
    public static TrackedValue Ref(TypeAnalysisContext type, string alias) => new(TrackedKind.ManagedRef, type, alias);
    public static TrackedValue Info(TypeAnalysisContext type) => new(TrackedKind.TypeInfo, type, null);
    public static TrackedValue StaticBaseOf(TypeAnalysisContext type) => new(TrackedKind.StaticBase, type, null);

    public bool IsKnown => Kind != TrackedKind.Unknown;
    public bool Equals(TrackedValue other) => Kind == other.Kind && SameType(Type, other.Type);
    public override bool Equals(object obj) => obj is TrackedValue value && Equals(value);
    public override int GetHashCode() => (int)Kind * 397; // Type intentionally excluded: wrapped types (arrays/pointers/generics) are not interned, so a stable hash must not depend on the instance. Equality does the real comparison.

    /// <summary>
    /// Semantic type identity for the dataflow meet. Cpp2IL interns definition-backed types
    /// (<see cref="TypeAnalysisContext.Definition"/> is a shared <c>Il2CppTypeDefinition</c>), but wrapped types
    /// — arrays, pointers, byref, generic instances — are re-created on every <c>ResolveIl2CppType</c>/<c>FieldType</c>
    /// access (see Il2CppTypeToContext.ResolveIl2CppType: WrappedTypeAnalysisContext.Create is uncached), so two
    /// resolutions of the same <c>T[]</c> are reference-distinct. A reference compare here would spuriously meet such a
    /// register to Unknown at any join, dropping a correct <c>array.Length</c>/field annotation. Compare by interned
    /// <c>Definition</c> when both have one; otherwise fall back to <c>FullName</c> (the only stable key for wrapped types).
    /// </summary>
    private static bool SameType(TypeAnalysisContext a, TypeAnalysisContext b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null)
            return false;
        if (a.Definition != null || b.Definition != null)
            return ReferenceEquals(a.Definition, b.Definition);
        return a.FullName == b.FullName;
    }
}

/// <summary>
/// Forward abstract interpretation over a method's decoded x86-64 instructions. Seeds argument registers from the
/// IL2CPP/MSVC (or SysV) calling convention, then propagates <see cref="TrackedValue"/>s through moves, field loads
/// and calls with a basic-block dataflow (meet = "agree or unknown"). The product is a per-instruction access
/// comment (<c>this.field</c>, <c>Type.staticField</c>, <c>array.Length</c>) recovered purely from IL2CPP metadata.
/// Register clobbering is precise (Iced <see cref="InstructionInfoFactory"/> write set + ABI-volatile set on calls),
/// so stale state can never mislabel a later access. Mirrors the seeding in Cpp2IL's X64CallingConventionResolver.
/// </summary>
internal sealed class Il2CppRegisterFlow
{
    private const int InstructionBudget = 12000;

    private readonly ApplicationAnalysisContext _app;
    private readonly MethodAnalysisContext _method;
    private readonly List<Instruction> _instructions;
    private readonly Il2CppTypeModel _model;
    private readonly bool _isPe;
    private readonly int _staticFieldsOffset;

    private readonly Dictionary<ulong, int> _indexByIp = new();
    private int[] _blockOf;
    private int[] _blockFirst;
    private List<int>[] _successors;
    private List<int>[] _predecessors;
    private TrackedValue[][] _entryState;
    private ushort[] _clobber;
    private bool[] _writesMemory;
    private string[] _comments;

    public Il2CppRegisterFlow(ApplicationAnalysisContext app, MethodAnalysisContext method, List<Instruction> instructions, Il2CppTypeModel model)
    {
        _app = app;
        _method = method;
        _instructions = instructions;
        _model = model;
        _isPe = app.Binary is PE;
        _staticFieldsOffset = model.StaticFieldsOffset;
    }

    /// <summary>Access comment for the instruction at <paramref name="index"/>, or null.</summary>
    public string CommentAt(int index) => _comments != null && (uint)index < (uint)_comments.Length ? _comments[index] : null;

    public void Analyze()
    {
        int n = _instructions.Count;
        if (n == 0 || n > InstructionBudget)
            return;
        try
        {
            for (int i = 0; i < n; i++)
                _indexByIp[_instructions[i].IP] = i;

            BuildBlocks();
            PrecomputeClobbers();
            RunDataflow();
            EmitComments();
        }
        catch
        {
            _comments = null; // annotation is best-effort; never break the listing
        }
    }

    private void BuildBlocks()
    {
        int n = _instructions.Count;
        bool[] leader = new bool[n];
        leader[0] = true;
        for (int i = 0; i < n; i++)
        {
            Instruction insn = _instructions[i];
            switch (insn.FlowControl)
            {
                case FlowControl.ConditionalBranch:
                    if (i + 1 < n) leader[i + 1] = true;
                    MarkTarget(insn, leader);
                    break;
                case FlowControl.UnconditionalBranch:
                    if (i + 1 < n) leader[i + 1] = true;
                    MarkTarget(insn, leader);
                    break;
                case FlowControl.Return:
                case FlowControl.IndirectBranch:
                case FlowControl.Interrupt:
                    if (i + 1 < n) leader[i + 1] = true;
                    break;
            }
        }

        _blockOf = new int[n];
        List<int> firsts = new();
        int blockId = -1;
        for (int i = 0; i < n; i++)
        {
            if (leader[i]) { blockId++; firsts.Add(i); }
            _blockOf[i] = blockId;
        }
        _blockFirst = firsts.ToArray();

        int blockCount = _blockFirst.Length;
        _successors = new List<int>[blockCount];
        _predecessors = new List<int>[blockCount];
        for (int b = 0; b < blockCount; b++) { _successors[b] = new List<int>(); _predecessors[b] = new List<int>(); }

        for (int b = 0; b < blockCount; b++)
        {
            int last = (b + 1 < blockCount ? _blockFirst[b + 1] : n) - 1;
            Instruction insn = _instructions[last];
            switch (insn.FlowControl)
            {
                case FlowControl.UnconditionalBranch:
                    if (_indexByIp.TryGetValue(insn.NearBranchTarget, out int t)) AddEdge(b, _blockOf[t]);
                    break;
                case FlowControl.ConditionalBranch:
                    if (last + 1 < n) AddEdge(b, _blockOf[last + 1]);
                    if (_indexByIp.TryGetValue(insn.NearBranchTarget, out int ct)) AddEdge(b, _blockOf[ct]);
                    break;
                case FlowControl.Return:
                case FlowControl.IndirectBranch:
                case FlowControl.Interrupt:
                    break;
                default:
                    if (last + 1 < n) AddEdge(b, _blockOf[last + 1]);
                    break;
            }
        }
    }

    private void MarkTarget(in Instruction insn, bool[] leader)
    {
        if (_indexByIp.TryGetValue(insn.NearBranchTarget, out int t))
            leader[t] = true;
    }

    private void AddEdge(int from, int to)
    {
        if (!_successors[from].Contains(to)) _successors[from].Add(to);
        if (!_predecessors[to].Contains(from)) _predecessors[to].Add(from);
    }

    private void PrecomputeClobbers()
    {
        int n = _instructions.Count;
        _clobber = new ushort[n];
        _writesMemory = new bool[n];
        ushort volatileMask = VolatileMask();
        InstructionInfoFactory factory = new();
        for (int i = 0; i < n; i++)
        {
            Instruction insn = _instructions[i];
            ushort mask = 0;
            InstructionInfo info = factory.GetInfo(insn);
            foreach (UsedRegister used in info.GetUsedRegisters())
            {
                if (!IsWrite(used.Access)) continue;
                int idx = RegisterFlowUtil.GpIndex(used.Register);
                if (idx >= 0) mask |= (ushort)(1 << idx);
            }
            // Whether this instruction actually STORES to a memory operand (mov [m],x / add [m],x …) as opposed to
            // merely reading it (cmp [m],x / test [m],x / a load). Drives the "field = value" store rendering so a
            // compare is never mislabeled as an assignment. Authoritative, from Iced's per-operand access.
            foreach (UsedMemory usedMemory in info.GetUsedMemory())
            {
                if (IsWrite(usedMemory.Access)) { _writesMemory[i] = true; break; }
            }
            if (insn.FlowControl == FlowControl.Call)
                mask |= volatileMask;
            _clobber[i] = mask;
        }
    }

    private ushort VolatileMask()
    {
        // Caller-saved GP registers. MSVC x64: rax,rcx,rdx,r8-r11. SysV additionally: rsi,rdi.
        ushort mask = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11);
        if (!_isPe) mask |= (1 << 6) | (1 << 7);
        return mask;
    }

    private static bool IsWrite(OpAccess access)
        => access is OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite;

    private void RunDataflow()
    {
        int blockCount = _blockFirst.Length;
        _entryState = new TrackedValue[blockCount][];
        _entryState[0] = Seed();

        Queue<int> worklist = new();
        worklist.Enqueue(0);
        bool[] queued = new bool[blockCount];
        queued[0] = true;

        int guard = 0;
        while (worklist.Count > 0 && guard++ < blockCount * 8 + 64)
        {
            int b = worklist.Dequeue();
            queued[b] = false;
            TrackedValue[] outState = TransferBlock(b, _entryState[b], null);
            foreach (int s in _successors[b])
            {
                TrackedValue[] merged = _entryState[s] == null ? (TrackedValue[])outState.Clone() : Meet(_entryState[s], outState);
                if (_entryState[s] == null || !StatesEqual(_entryState[s], merged))
                {
                    _entryState[s] = merged;
                    if (!queued[s]) { worklist.Enqueue(s); queued[s] = true; }
                }
            }
        }
    }

    private void EmitComments()
    {
        int n = _instructions.Count;
        _comments = new string[n];
        int blockCount = _blockFirst.Length;
        for (int b = 0; b < blockCount; b++)
        {
            if (_entryState[b] == null) continue; // unreachable
            TransferBlock(b, _entryState[b], _comments);
        }
    }

    /// <summary>Walks a block from its entry state; when <paramref name="commentsOut"/> is non-null, records the access comment for each instruction.</summary>
    private TrackedValue[] TransferBlock(int block, TrackedValue[] entry, string[] commentsOut)
    {
        TrackedValue[] state = (TrackedValue[])entry.Clone();
        int start = _blockFirst[block];
        int end = block + 1 < _blockFirst.Length ? _blockFirst[block + 1] : _instructions.Count;
        for (int i = start; i < end; i++)
        {
            Instruction insn = _instructions[i];
            if (commentsOut != null)
                commentsOut[i] = BuildComment(insn, i, state);
            TransferInstruction(insn, i, state);
        }
        return state;
    }

    private void TransferInstruction(in Instruction insn, int index, TrackedValue[] state)
    {
        int dst = -1;
        TrackedValue newValue = TrackedValue.Unknown;
        bool hasNew = false;

        switch (insn.Mnemonic)
        {
            case Mnemonic.Mov:
            case Mnemonic.Movzx:
            case Mnemonic.Movsx:
            case Mnemonic.Movsxd:
                if (insn.Op0Kind == OpKind.Register)
                {
                    dst = RegisterFlowUtil.GpIndex(insn.Op0Register);
                    if (dst >= 0)
                    {
                        hasNew = true;
                        if (insn.Op1Kind == OpKind.Register)
                        {
                            int src = RegisterFlowUtil.GpIndex(insn.Op1Register);
                            newValue = src >= 0 ? state[src] : TrackedValue.Unknown;
                        }
                        else if (insn.Op1Kind == OpKind.Memory)
                        {
                            newValue = EvalMemory(insn, state, isLea: false);
                        }
                        else
                        {
                            newValue = TrackedValue.Unknown;
                        }
                    }
                }
                break;
            case Mnemonic.Lea:
                if (insn.Op0Kind == OpKind.Register)
                {
                    dst = RegisterFlowUtil.GpIndex(insn.Op0Register);
                    if (dst >= 0) { hasNew = true; newValue = EvalMemory(insn, state, isLea: true); }
                }
                break;
        }

        ushort mask = _clobber[index];
        for (int r = 0; r < 16; r++)
        {
            if ((mask & (1 << r)) != 0) state[r] = TrackedValue.Unknown;
        }

        if (insn.FlowControl == FlowControl.Call)
        {
            state[0] = CallReturn(insn); // rax
            return;
        }

        if (hasNew && dst >= 0)
            state[dst] = newValue;
    }

    private TrackedValue CallReturn(in Instruction insn)
    {
        if (insn.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
            return TrackedValue.Unknown;
        if (!_app.MethodsByAddress.TryGetValue(insn.NearBranchTarget, out List<MethodAnalysisContext> methods) || methods.Count == 0)
            return TrackedValue.Unknown;
        TypeAnalysisContext returnType = methods[0].ReturnType;
        if (returnType == null || returnType.IsValueType)
            return TrackedValue.Unknown;
        return TrackedValue.Ref(returnType, null);
    }

    private TrackedValue EvalMemory(in Instruction insn, TrackedValue[] state, bool isLea)
    {
        if (insn.IsIPRelativeMemoryOperand || (insn.MemoryBase == Register.None && insn.MemoryIndex == Register.None))
        {
            if (!isLea && _model.TryGetTypeForTypeInfoGlobal(insn.MemoryDisplacement64, out TypeAnalysisContext infoType))
                return TrackedValue.Info(infoType);
            return TrackedValue.Unknown;
        }
        if (insn.MemoryIndex != Register.None)
            return TrackedValue.Unknown; // indexed access is not a plain field

        int baseIndex = RegisterFlowUtil.GpIndex(insn.MemoryBase);
        if (baseIndex < 0)
            return TrackedValue.Unknown;

        TrackedValue baseValue = state[baseIndex];
        int disp = (int)insn.MemoryDisplacement64;
        switch (baseValue.Kind)
        {
            case TrackedKind.ManagedRef when _model.TryGetInstanceField(baseValue.Type, disp, out FieldAnalysisContext field):
                return (isLea || !field.FieldType.IsValueType)
                    ? TrackedValue.Ref(field.FieldType, Combine(baseValue.Alias, field.Name))
                    : TrackedValue.Unknown;
            case TrackedKind.StaticBase when _model.TryGetStaticField(baseValue.Type, disp, out FieldAnalysisContext staticField):
                return (isLea || !staticField.FieldType.IsValueType)
                    ? TrackedValue.Ref(staticField.FieldType, baseValue.Type.Name + "." + staticField.Name)
                    : TrackedValue.Unknown;
            case TrackedKind.TypeInfo when _staticFieldsOffset >= 0 && disp == _staticFieldsOffset:
                return TrackedValue.StaticBaseOf(baseValue.Type);
            default:
                return TrackedValue.Unknown;
        }
    }

    private string BuildComment(in Instruction insn, int index, TrackedValue[] state)
    {
        int memoryOp = -1;
        for (int k = 0; k < insn.OpCount; k++)
        {
            if (insn.GetOpKind(k) == OpKind.Memory) { memoryOp = k; break; }
        }
        if (memoryOp < 0)
            return null;

        int baseIndex = RegisterFlowUtil.GpIndex(insn.MemoryBase);
        if (baseIndex < 0)
            return null;
        TrackedValue baseValue = state[baseIndex];
        if (!baseValue.IsKnown)
            return null;

        int disp = (int)insn.MemoryDisplacement64;
        // A store only when the memory operand is the destination AND Iced confirms it is actually written
        // (rules out cmp/test [mem],x which read operand 0). Prevents rendering a compare as "field = value".
        bool isWrite = memoryOp == 0 && _writesMemory != null && (uint)index < (uint)_writesMemory.Length && _writesMemory[index];
        string access = null;

        if (baseValue.Kind == TrackedKind.ManagedRef)
        {
            if (IsArrayLike(baseValue.Type))
            {
                if (insn.MemoryIndex == Register.None && disp == 0x18)
                    access = Combine(baseValue.Alias, "Length");
                else if (insn.MemoryIndex != Register.None && disp >= 0x20)
                    access = (baseValue.Alias ?? "array") + "[i]";
            }
            else if (insn.MemoryIndex == Register.None && _model.TryGetInstanceField(baseValue.Type, disp, out FieldAnalysisContext field))
            {
                access = Combine(baseValue.Alias, field.Name);
            }
        }
        else if (baseValue.Kind == TrackedKind.StaticBase && insn.MemoryIndex == Register.None
                 && _model.TryGetStaticField(baseValue.Type, disp, out FieldAnalysisContext staticField))
        {
            access = baseValue.Type.Name + "." + staticField.Name;
        }
        else if (baseValue.Kind == TrackedKind.TypeInfo && _staticFieldsOffset >= 0 && disp == _staticFieldsOffset)
        {
            access = "&" + baseValue.Type.Name + "::static_fields";
        }

        if (access == null)
            return null;
        if (!isWrite)
            return access;
        return access + " = " + SourceToken(insn, state);
    }

    private static string SourceToken(in Instruction insn, TrackedValue[] state)
    {
        // The store's source is the non-memory operand (operand 1 for a `mov [mem], x`).
        if (insn.Op1Kind == OpKind.Register)
        {
            int src = RegisterFlowUtil.GpIndex(insn.Op1Register);
            if (src >= 0 && state[src].IsKnown && state[src].Alias != null)
                return state[src].Alias;
            return insn.Op1Register.ToString().ToLowerInvariant();
        }
        if (IsImmediate(insn.Op1Kind))
            return "0x" + insn.GetImmediate(1).ToString("X");
        return "…";
    }

    private static bool IsImmediate(OpKind kind)
        => kind is OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64
            or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate32to64 or OpKind.Immediate64;

    private static string Combine(string alias, string member) => (alias ?? "?") + "." + member;

    private static bool IsArrayLike(TypeAnalysisContext type)
        => type is ArrayTypeAnalysisContext or SzArrayTypeAnalysisContext;

    private TrackedValue[] Seed()
    {
        TrackedValue[] state = new TrackedValue[16];
        Il2CppBinary binary = _app.Binary;
        if (_method == null || binary == null || binary.is32Bit)
            return state;

        bool addThis = !_method.IsStatic;
        // A value type too big for a register is returned through a hidden first-arg pointer, shifting this/args by one.
        bool hiddenReturn = _model.IsReturnedViaHiddenPointer(_method.ReturnType);
        if (_isPe)
        {
            int slot = 0;
            if (hiddenReturn) { state[MsvcSlotReg(0)] = TrackedValue.Ref(_method.ReturnType, "retval"); slot = 1; }
            if (addThis)
            {
                int reg = MsvcSlotReg(slot);
                if (reg >= 0 && _method.DeclaringType != null) state[reg] = TrackedValue.Ref(_method.DeclaringType, "this");
                slot++;
            }
            for (int p = 0; p < _method.Parameters.Count; p++, slot++)
            {
                int reg = MsvcSlotReg(slot);
                if (reg < 0) break; // slot >= 4 spills to stack; not tracked
                ParameterAnalysisContext par = _method.Parameters[p];
                if (IsSeedableRef(par.ParameterType)) state[reg] = TrackedValue.Ref(par.ParameterType, par.Name ?? ("arg" + p));
            }
        }
        else
        {
            int nreg = 0; // rdi,rsi,rdx,rcx,r8,r9
            if (hiddenReturn) { state[ElfSlotReg(0)] = TrackedValue.Ref(_method.ReturnType, "retval"); nreg = 1; }
            if (addThis)
            {
                int reg = ElfSlotReg(nreg);
                if (reg >= 0 && _method.DeclaringType != null) state[reg] = TrackedValue.Ref(_method.DeclaringType, "this");
                nreg++;
            }
            foreach (ParameterAnalysisContext par in _method.Parameters)
            {
                if (IsFloat(par.ParameterType)) continue; // SysV floats use xmm, not the integer sequence
                int reg = ElfSlotReg(nreg);
                if (reg < 0) break;
                if (IsSeedableRef(par.ParameterType)) state[reg] = TrackedValue.Ref(par.ParameterType, par.Name ?? "arg");
                nreg++;
            }
        }
        return state;
    }

    private static int MsvcSlotReg(int slot) => slot switch { 0 => 1, 1 => 2, 2 => 8, 3 => 9, _ => -1 }; // rcx,rdx,r8,r9
    private static int ElfSlotReg(int slot) => slot switch { 0 => 7, 1 => 6, 2 => 2, 3 => 1, 4 => 8, 5 => 9, _ => -1 }; // rdi,rsi,rdx,rcx,r8,r9

    private bool IsSeedableRef(TypeAnalysisContext type)
        => type != null && !type.IsValueType; // only object pointers hold field-addressable references

    private bool IsFloat(TypeAnalysisContext type)
        => type != null && (type == _app.SystemTypes.SystemSingleType || type == _app.SystemTypes.SystemDoubleType);

    private TrackedValue[] Meet(TrackedValue[] a, TrackedValue[] b)
    {
        TrackedValue[] result = new TrackedValue[16];
        for (int i = 0; i < 16; i++)
            result[i] = a[i].Equals(b[i]) ? a[i] : TrackedValue.Unknown;
        return result;
    }

    private static bool StatesEqual(TrackedValue[] a, TrackedValue[] b)
    {
        for (int i = 0; i < 16; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }
}
