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

internal enum TrackedKind : byte { Unknown, ManagedRef, TypeInfo, StaticBase, Klass, Callee }

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
    public static TrackedValue KlassOf(TypeAnalysisContext type) => new(TrackedKind.Klass, type, null); // Il2CppClass* of an object (obtained by dereferencing it at offset 0)
    public static TrackedValue Callee(TypeAnalysisContext returnType) => new(TrackedKind.Callee, returnType, null); // a loaded virtual function pointer; Type = its return type (materialized on `call`)

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
    private readonly ulong _objectNewAddress; // il2cpp_codegen_object_new: (Il2CppClass* in rcx) -> new object

    private readonly Dictionary<ulong, int> _indexByIp = new();
    private int[] _blockOf;
    private int[] _blockFirst;
    private List<int>[] _successors;
    private List<int>[] _predecessors;
    private TrackedValue[][] _entryState;
    private ushort[] _clobber;
    private bool[] _writesMemory;
    private string[] _comments;
    // Named vtable arrows whose slot method returns void/non-reference — candidates for retraction if the call's result
    // (rax) turns out to be consumed as a managed reference downstream (a self-contradiction => mis-mapped slot name).
    // fnReg: the register a fn-ptr load targets (its `call reg` is found later), or -1 when the arrow instruction is the
    // indirect dispatch itself (`call [klass+disp]`). disp/typeName reconstruct the honest T::class[0xNN] fallback and
    // let a failing call retract its paired MethodInfo load (`mov reg,[klass+disp+8]`, same slot, same wrong name).
    // kind: the named slot's return kind (Il2CppTypeModel.ReturnKind*) — selects the contradiction test that can never
    // fire on a correctly-named method (void/scalar: rax can't be dereferenced or full-width-stored; ref: its offset-0
    // can't be a float; struct/pointer: rax is legitimately a pointer, so no test).
    private List<(int index, int fnReg, int disp, string typeName, byte kind)> _arrowRetractCandidates;

    public Il2CppRegisterFlow(ApplicationAnalysisContext app, MethodAnalysisContext method, List<Instruction> instructions, Il2CppTypeModel model)
    {
        _app = app;
        _method = method;
        _instructions = instructions;
        _model = model;
        _isPe = app.Binary is PE;
        _staticFieldsOffset = model.StaticFieldsOffset;
        _objectNewAddress = Il2CppAsmAnnotator.KeyFunctionAddress(app, "codegen_object_new");
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
            RetractInconsistentArrows();
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
            // Whether this instruction actually STORES to its DISPLAYED memory operand (mov [m],x / add [m],x …) as
            // opposed to merely reading it (cmp [m],x / a load / an indirect call). Matched to the operand's own base/index
            // so an incidental stack write (the return-address push of `call [m]`, base=rsp) never counts. Drives the
            // "field = value" store rendering so neither a compare nor a virtual call is mislabeled as an assignment.
            foreach (UsedMemory usedMemory in info.GetUsedMemory())
            {
                if (IsWrite(usedMemory.Access) && usedMemory.Base == insn.MemoryBase && usedMemory.Index == insn.MemoryIndex)
                {
                    _writesMemory[i] = true;
                    break;
                }
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

    /// <summary>
    /// Self-consistency pass: a named vtable arrow whose slot method returns void/non-reference, but whose call result
    /// (<c>rax</c>) is then consumed as a managed reference (stored full-width to memory, or dereferenced), is a
    /// contradiction the tool can detect against its own downstream annotations — the slot name is wrong for this call
    /// site. Downgrade it to the honest <c>T::class[0xNN]</c> rather than assert a fabricated name (错标比漏标更糟).
    /// </summary>
    private void RetractInconsistentArrows()
    {
        if (_arrowRetractCandidates == null || _comments == null) return;
        // Phase 1: condemn each (type, vtable slot) proven mis-mapped — a dispatch whose non-reference result is consumed
        // as a managed reference. The slot's name is metadata, identical at every call site, so one proof condemns it for
        // the whole method; a slot never contradicted (e.g. a genuine void setter whose result is ignored) stays named.
        HashSet<(string, int)> condemned = null;
        foreach ((int index, int fnReg, int disp, string typeName, byte kind) in _arrowRetractCandidates)
        {
            if ((uint)index < (uint)_comments.Length && DispatchResultContradicts(index, fnReg, kind))
            {
                (condemned ??= new()).Add((typeName, SlotKey(disp)));
                _model.CondemnedVtableSlots.Add((typeName, SlotKey(disp))); // app-wide: condemn this slot for every later method too
            }
        }
        if (condemned == null) return;
        // Phase 2: retract every arrow (call, fn-ptr load, and paired MethodInfo load) of a condemned (type, slot).
        foreach ((int index, int _, int disp, string typeName, byte _) in _arrowRetractCandidates)
        {
            if ((uint)index < (uint)_comments.Length && _comments[index] != null && _comments[index].StartsWith("-> ")
                && condemned.Contains((typeName, SlotKey(disp))))
                _comments[index] = typeName + "::class[0x" + disp.ToString("X") + "]";
        }
    }

    /// <summary>Folds a klass byte offset to its 0x10-aligned vtable slot base so the methodPtr (<c>+0</c>) and MethodInfo (<c>+8</c>) reads of one call group equal — used only to co-retract a call's arrows (not the authoritative slot map).</summary>
    private static int SlotKey(int disp) => disp & ~0xF;

    /// <summary>
    /// From a vtable dispatch — either a fn-ptr load at <paramref name="arrowIndex"/> whose pointer is in
    /// <paramref name="fnReg"/> then a later <c>call fnReg</c>, or (when <paramref name="fnReg"/> is -1) the arrow
    /// instruction being itself an indirect <c>call [klass+disp]</c> — decide whether the call's result (<c>rax</c>)
    /// contradicts the named slot's declared return <paramref name="kind"/>, before <c>rax</c> is redefined. Each test
    /// is chosen so it can NEVER fire on a correctly-named method (so a good name is never downgraded):
    /// <list type="bullet">
    /// <item>Void / Scalar (bool/int/float/enum): <c>rax</c> stored full 64-bit (<c>mov [m],rax</c>) or dereferenced
    /// (<c>[rax…]</c>) — void yields nothing and a scalar leaves a non-pointer value in rax, so neither can be a base.</item>
    /// <item>Ref: offset-0 of <c>rax</c> read as a float (<c>movss/movsd xmm,[rax]</c>) — a real object's slot 0 is its
    /// <c>Il2CppClass*</c>, never float data, so the result is actually a value/struct-return buffer.</item>
    /// <item>Struct (hidden-buffer pointer) / Pointer (IntPtr) / Unresolved: rax is legitimately a pointer — no test.</item>
    /// </list>
    /// Raw instruction shapes only (no dataflow state); small windows that bail on any intervening clobber/call.
    /// </summary>
    private bool DispatchResultContradicts(int arrowIndex, int fnReg, byte kind)
    {
        bool voidOrScalar = kind is Il2CppTypeModel.ReturnKindVoid or Il2CppTypeModel.ReturnKindScalar;
        bool isRef = kind == Il2CppTypeModel.ReturnKindRef;
        if (!voidOrScalar && !isRef)
            return false; // struct buffer / IntPtr / unresolved — rax is a legitimate pointer, nothing to contradict

        int n = _instructions.Count;
        int callIdx;
        if (fnReg < 0)
        {
            callIdx = arrowIndex; // the arrow instruction is the indirect call itself (`call [klass+disp]`)
        }
        else
        {
            callIdx = -1;
            for (int j = arrowIndex + 1; j < n && j <= arrowIndex + 10; j++)
            {
                Instruction c = _instructions[j];
                // The fn ptr loaded into fnReg is invoked by `call fnReg` (an IndirectCall on a register operand).
                if (c.FlowControl == FlowControl.IndirectCall && c.Op0Kind == OpKind.Register
                    && RegisterFlowUtil.GpIndex(c.Op0Register) == fnReg)
                { callIdx = j; break; }
                if (c.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.IndirectBranch)
                    return false;                                              // some other call/tail-branch first — can't attribute rax
                if ((_clobber[j] & (1 << fnReg)) != 0) return false;           // fn-ptr register overwritten before the call
            }
            if (callIdx < 0) return false;
        }

        for (int j = callIdx + 1; j < n && j <= callIdx + 8; j++)
        {
            Instruction u = _instructions[j];
            if (voidOrScalar)
            {
                // rax used as a memory base/index (dereferenced) — a void/scalar result is not a pointer.
                if (u.MemoryBase.GetFullRegister() == Register.RAX || u.MemoryIndex.GetFullRegister() == Register.RAX)
                    return true;
                // rax stored full 64-bit to memory — a managed reference a void/scalar method cannot have produced.
                if (u.Mnemonic == Mnemonic.Mov && u.Op0Kind == OpKind.Memory
                    && u.Op1Kind == OpKind.Register && u.Op1Register == Register.RAX)
                    return true;
            }
            else // isRef
            {
                // Reference return whose offset-0 is loaded as a float — impossible for a real object (slot 0 = klass ptr).
                if ((u.Mnemonic is Mnemonic.Movss or Mnemonic.Movsd) && u.Op1Kind == OpKind.Memory
                    && u.MemoryBase.GetFullRegister() == Register.RAX && u.MemoryIndex == Register.None
                    && u.MemoryDisplacement64 == 0)
                    return true;
            }
            // rax redefined/clobbered (incl. any further call) — stop this scan, but still try the `this`-of-call scan
            // below (it survives the clobber once rax has been moved into rcx).
            if ((_clobber[j] & (1 << 0)) != 0)
                break;
        }
        // Void/scalar result passed as the `this` of a managed instance call (`mov rcx,rax; …; call Instance.Method`) —
        // a `this` must be a valid object reference, which neither void (nothing) nor a scalar value can be. This survives
        // rax being clobbered once it has been moved into rcx, so it is a separate forward scan.
        return voidOrScalar && RaxBecomesThisOfManagedInstanceCall(callIdx);
    }

    private bool RaxBecomesThisOfManagedInstanceCall(int callIdx)
    {
        // Generous window: as long as rax is never clobbered before it reaches rcx (checked below), it is still the
        // dispatch result no matter the distance — inlined bounds/null checks can separate the call from the `mov rcx,rax`.
        int n = _instructions.Count;
        bool inRcx = false;
        for (int j = callIdx + 1; j < n && j <= callIdx + 20; j++)
        {
            Instruction u = _instructions[j];
            if (!inRcx)
            {
                if (u.Mnemonic == Mnemonic.Mov && u.Op0Kind == OpKind.Register && u.Op0Register == Register.RCX
                    && u.Op1Kind == OpKind.Register && u.Op1Register == Register.RAX)
                { inRcx = true; continue; }
                if ((_clobber[j] & (1 << 0)) != 0) return false; // rax clobbered before it reaches rcx
            }
            else
            {
                // rcx now holds the result; a managed *instance* call consumes it as `this` (rcx must be a reference).
                if (u.FlowControl == FlowControl.Call && u.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32
                    && _app.MethodsByAddress.TryGetValue(u.NearBranchTarget, out List<MethodAnalysisContext> callee)
                    && callee.Count > 0 && !callee[0].IsStatic)
                    return true;
                if ((_clobber[j] & (1 << 1)) != 0) return false; // rcx reclobbered before the call
            }
        }
        return false;
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

        // Allocation: `mov rcx,[T_TypeInfo]; call il2cpp_codegen_object_new` -> rax = new T (read rcx before it is clobbered).
        bool isAlloc = false;
        TrackedValue allocResult = TrackedValue.Unknown;
        if (insn.FlowControl == FlowControl.Call && _objectNewAddress != 0
            && insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64
            && insn.NearBranchTarget == _objectNewAddress
            && state[1].Kind == TrackedKind.TypeInfo && state[1].Type != null)
        {
            isAlloc = true;
            allocResult = TrackedValue.Ref(state[1].Type, "new " + state[1].Type.Name);
        }
        // Indirect virtual call: `call reg` where reg holds a vtable fn ptr -> rax = its (reference) return type.
        else if (insn.FlowControl == FlowControl.Call && insn.Op0Kind == OpKind.Register)
        {
            int calleeReg = RegisterFlowUtil.GpIndex(insn.Op0Register);
            if (calleeReg >= 0 && state[calleeReg].Kind == TrackedKind.Callee && state[calleeReg].Type != null)
            {
                isAlloc = true;
                allocResult = TrackedValue.Ref(state[calleeReg].Type, null);
            }
        }

        ushort mask = _clobber[index];
        for (int r = 0; r < 16; r++) // only the 16 GP registers are clobbered; frame slots (16+) are memory, preserved across calls
        {
            if ((mask & (1 << r)) != 0) state[r] = TrackedValue.Unknown;
        }

        if (insn.FlowControl == FlowControl.Call)
        {
            state[0] = isAlloc ? allocResult : CallReturn(insn); // rax
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
        {
            // Array element load: `[arr + idx*8 + 0x20]` where arr is an array -> the element's (reference) type, so
            // a field access on the loaded element resolves.
            int arrayBase = RegisterFlowUtil.GpIndex(insn.MemoryBase);
            if (!isLea && arrayBase >= 0 && (int)insn.MemoryDisplacement64 >= 0x20
                && state[arrayBase].Kind == TrackedKind.ManagedRef && IsArrayLike(state[arrayBase].Type)
                && state[arrayBase].Type is WrappedTypeAnalysisContext wrapped && wrapped.ElementType is { } element && !element.IsValueType)
                return TrackedValue.Ref(element, (state[arrayBase].Alias ?? "array") + "[i]");
            return TrackedValue.Unknown;
        }

        int baseIndex = RegisterFlowUtil.GpIndex(insn.MemoryBase);
        if (baseIndex < 0)
            return TrackedValue.Unknown;

        TrackedValue baseValue = state[baseIndex];
        int disp = (int)insn.MemoryDisplacement64;
        switch (baseValue.Kind)
        {
            // Dereferencing a reference-type object at offset 0 loads its Il2CppClass* (the object header slot) — the base for a vtable dispatch.
            case TrackedKind.ManagedRef when !isLea && disp == 0 && !baseValue.Type.IsValueType:
                return TrackedValue.KlassOf(baseValue.Type);
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
            // Loading a virtual function pointer from the vtable: remember its (reference) return type for the following `call`.
            case TrackedKind.Klass when !isLea && _model.TryGetVirtualReturnType(baseValue.Type, disp, out TypeAnalysisContext vret):
                return TrackedValue.Callee(vret);
            default:
                return TrackedValue.Unknown;
        }
    }

    private string BuildComment(in Instruction insn, int index, TrackedValue[] state)
    {
        // Allocation call: `mov rcx,[T_TypeInfo]; call il2cpp_codegen_object_new` -> rax = new T.
        if (insn.FlowControl == FlowControl.Call && _objectNewAddress != 0
            && insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64
            && insn.NearBranchTarget == _objectNewAddress
            && state[1].Kind == TrackedKind.TypeInfo && state[1].Type != null)
            return "rax = new " + state[1].Type.Name + "()";

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
        else if (baseValue.Kind == TrackedKind.Klass && insn.MemoryIndex == Register.None
                 && _model.TryGetVirtualMethodName(baseValue.Type, disp, out string virtualMethod))
        {
            // Already proven mis-mapped for this (type, slot) elsewhere in the app — emit the honest fallback up front
            // (root cut: no arrow, no candidate), so every site reads consistently regardless of local evidence.
            if (_model.CondemnedVtableSlots.Contains((baseValue.Type.Name, SlotKey(disp))))
            {
                access = baseValue.Type.Name + "::class[0x" + disp.ToString("X") + "]";
            }
            else
            {
                access = "-> " + virtualMethod; // virtual/interface dispatch through the object's vtable
                // Consistency gate: if this slot's method returns void/non-reference yet its result (rax) is consumed as a
                // managed reference downstream, the name is wrong for this call site (mis-mapped vtable/interface slot).
                // Record it; RetractInconsistentArrows downgrades it to the honest T::class[0xNN] if the contradiction holds.
                // Two idiom shapes reach here: a fn-ptr load `mov reg,[klass+disp]` (fnReg = dst, its `call reg` is later) and
                // a direct dispatch `call [klass+disp]` (FlowControl.IndirectCall; fnReg = -1). A tail-call `jmp [klass+disp]`
                // (IndirectBranch) returns straight to our caller, so its rax use is unobservable here — not a candidate.
                if (insn.FlowControl != FlowControl.IndirectBranch)
                {
                    int fnReg = insn.FlowControl == FlowControl.IndirectCall ? -1 : RegisterFlowUtil.GpIndex(insn.Op0Register);
                    if (fnReg >= 0 || insn.FlowControl == FlowControl.IndirectCall)
                        (_arrowRetractCandidates ??= new()).Add((index, fnReg, disp, baseValue.Type.Name,
                            _model.GetVirtualReturnKind(baseValue.Type, disp)));
                }
            }
        }
        else if (baseValue.Kind is TrackedKind.TypeInfo or TrackedKind.Klass && insn.MemoryIndex == Register.None)
        {
            // Any other read off a known Il2CppClass* is the runtime class struct (init flags/state, rgctx, vtable count…);
            // not a managed field, but the owning type IS metadata — name it so the class-init guard reads clearly.
            access = baseValue.Type.Name + "::class[0x" + disp.ToString("X") + "]";
        }

        if (access == null)
            return null;
        if (!isWrite)
            return access;
        if (insn.Mnemonic == Mnemonic.Inc)
            return access + "++";
        if (insn.Mnemonic == Mnemonic.Dec)
            return access + "--";
        // Only a pure `mov [field], x` store renders as an assignment; other read-modify-writes (add/or/shl [field],x)
        // just name the field (the mnemonic already shows the operation) — never fabricate a "= source".
        string source = IsStoreMov(insn.Mnemonic) ? SourceToken(insn, state) : null;
        return source == null ? access : access + " = " + source;
    }

    private static bool IsStoreMov(Mnemonic mnemonic)
        => mnemonic is Mnemonic.Mov or Mnemonic.Movss or Mnemonic.Movsd or Mnemonic.Movaps or Mnemonic.Movups
            or Mnemonic.Movdqa or Mnemonic.Movdqu or Mnemonic.Movq or Mnemonic.Movd;

    private static string SourceToken(in Instruction insn, TrackedValue[] state)
    {
        // The store's source is the non-memory operand (operand 1 for a `mov [mem], x`).
        if (insn.Op1Kind == OpKind.Register)
        {
            int src = RegisterFlowUtil.GpIndex(insn.Op1Register);
            if (src >= 0 && state[src].IsKnown && state[src].Alias != null)
                return state[src].Alias;
            return insn.Op1Register == Register.None ? null : insn.Op1Register.ToString().ToLowerInvariant();
        }
        if (IsImmediate(insn.Op1Kind))
            return "0x" + insn.GetImmediate(1).ToString("X");
        return null;
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

    private static TrackedValue[] Meet(TrackedValue[] a, TrackedValue[] b)
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
