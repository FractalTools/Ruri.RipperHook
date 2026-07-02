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
    // When this value is the (reference) result of a vtable dispatch, the (klass type name, 0x10-aligned slot) it came
    // from — so if it is later stored into a field of an incompatible type, that slot can be condemned. Excluded from
    // equality (like Alias), so the dataflow meet still converges.
    public readonly string OriginTypeName;
    public readonly int OriginSlot;

    private TrackedValue(TrackedKind kind, TypeAnalysisContext type, string alias, string originTypeName = null, int originSlot = -1)
    {
        Kind = kind;
        Type = type;
        Alias = alias;
        OriginTypeName = originTypeName;
        OriginSlot = originSlot;
    }

    public static readonly TrackedValue Unknown = default;
    public static TrackedValue Ref(TypeAnalysisContext type, string alias) => new(TrackedKind.ManagedRef, type, alias);
    public static TrackedValue RefFromVtable(TypeAnalysisContext type, string originTypeName, int originSlot) => new(TrackedKind.ManagedRef, type, null, originTypeName, originSlot);
    public static TrackedValue Info(TypeAnalysisContext type) => new(TrackedKind.TypeInfo, type, null);
    public static TrackedValue StaticBaseOf(TypeAnalysisContext type) => new(TrackedKind.StaticBase, type, null);
    public static TrackedValue KlassOf(TypeAnalysisContext type) => new(TrackedKind.Klass, type, null); // Il2CppClass* of an object (obtained by dereferencing it at offset 0)
    public static TrackedValue Callee(TypeAnalysisContext returnType, string originTypeName, int originSlot) => new(TrackedKind.Callee, returnType, null, originTypeName, originSlot); // a loaded virtual function pointer; Type = its return type (materialized on `call`); carries the slot it was loaded from

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
    private List<(int index, int fnReg, int disp, string typeName, byte kind, string methodName)> _arrowRetractCandidates;

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
            // A call clobbers the ABI-volatile registers. This MUST include INDIRECT calls (`call reg` / `call [klass+disp]`
            // virtual dispatch) — omitting them let a stale reference in rax survive the call and mislabel the next deref
            // (e.g. a BaseInput left in rax by get_input() read as UnityEngine.Object.m_CachedPtr after the vtable call).
            // A genuine reference result is re-established afterwards from the callee's return type (CallReturn / directVtableRef).
            if (insn.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
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
        foreach ((int index, int fnReg, int disp, string typeName, byte kind, string methodName) in _arrowRetractCandidates)
        {
            if ((uint)index < (uint)_comments.Length && DispatchResultContradicts(index, fnReg, kind))
            {
                (condemned ??= new()).Add((typeName, SlotKey(disp)));
                _model.CondemnedVtableSlots.Add((typeName, SlotKey(disp)));      // app-wide: this slot on this receiver type
                if (methodName != null)
                    _model.CondemnedVtableMethods.Add((methodName, SlotKey(disp))); // and this inherited method at this slot, on any receiver
            }
        }
        if (condemned == null) return;
        // Phase 2: retract every arrow (call, fn-ptr load, and paired MethodInfo load) of a condemned (type, slot).
        foreach ((int index, int _, int disp, string typeName, byte _, string _) in _arrowRetractCandidates)
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
    /// contradicts the named slot's declared return <paramref name="kind"/>, before the result register is redefined.
    /// Each test can only fire when the observed usage is impossible for the named kind (so a good name is never lost):
    /// <list type="bullet">
    /// <item>Void / ScalarInt (bool/int/enum/char): <c>rax</c> dereferenced (<c>[rax…]</c>) or stored full 64-bit
    /// (<c>mov [m],rax</c>) or passed as a call <c>this</c> — none is possible for nothing / a non-pointer integer; OR
    /// <c>xmm0</c> consumed — the result would then be a float, not int/void.</item>
    /// <item>ScalarFloat (float/double): result is in <c>xmm0</c>, so <c>rax</c> is garbage — any rax deref / full store /
    /// use-as-<c>this</c> is impossible for it.</item>
    /// <item>Ref: <c>[rax]</c> (slot-0 = <c>Il2CppClass*</c>) read as a float, or <c>xmm0</c> consumed — a reference is a
    /// pointer in rax, never a float and never in xmm0.</item>
    /// <item>Struct (hidden-buffer pointer) / Pointer (IntPtr) / Unresolved: rax is legitimately a pointer — no test.</item>
    /// </list>
    /// Raw instruction shapes only (no dataflow state); small windows that bail on any intervening clobber/call.
    /// </summary>
    private bool DispatchResultContradicts(int arrowIndex, int fnReg, byte kind)
    {
        // Result location per the x64 ABI: an integer/void leaves rax an integer or nothing; a float leaves it in xmm0
        // (rax garbage); a reference leaves an object pointer in rax. Each test below can only fire when the actual usage
        // is impossible for the named kind — so a correctly-named method is never downgraded.
        // raxIntLike: result (if any) is a non-pointer in rax — void/int/bool. isFloat: in xmm0 (rax garbage). isRef:
        // object pointer in rax. (bool joins raxIntLike for the pointer/xmm0 tests; only the `test al,al` bool-signal is
        // suppressed for a genuine bool — see below.)
        bool raxIntLike = kind is Il2CppTypeModel.ReturnKindVoid or Il2CppTypeModel.ReturnKindScalarInt or Il2CppTypeModel.ReturnKindBool;
        bool isFloat = kind == Il2CppTypeModel.ReturnKindScalarFloat;
        bool isRef = kind == Il2CppTypeModel.ReturnKindRef;
        bool isStruct = kind == Il2CppTypeModel.ReturnKindStruct;
        // A bare 32-bit `eax`-as-value use is impossible for anything that does NOT return an int32 in eax (a Bool leaves
        // its result in al and legitimately widens it). Catches struct/ref/float/void getters mislabeled over an int
        // getter (e.g. Vector2 get_mouseScrollDelta whose result is a `cmp edi,eax` loop bound = the real touchCount:int).
        bool eaxAsIntContradicts = kind is Il2CppTypeModel.ReturnKindVoid or Il2CppTypeModel.ReturnKindScalarFloat
            or Il2CppTypeModel.ReturnKindRef or Il2CppTypeModel.ReturnKindStruct;
        if (!raxIntLike && !isFloat && !isRef && !isStruct)
            return false; // IntPtr / unresolved — rax is a legitimate pointer, nothing to contradict

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

        bool raxLive = true, xmm0Live = true;
        for (int j = callIdx + 1; j < n && j <= callIdx + 16; j++)
        {
            Instruction u = _instructions[j];
            if (raxLive && (raxIntLike || isFloat))
            {
                // int/bool/void/float leaves no object pointer in rax — a deref or full-rax store is impossible for it.
                if (u.MemoryBase.GetFullRegister() == Register.RAX || u.MemoryIndex.GetFullRegister() == Register.RAX)
                    return true;
                if (u.Mnemonic == Mnemonic.Mov && u.Op0Kind == OpKind.Memory
                    && u.Op1Kind == OpKind.Register && u.Op1Register == Register.RAX)
                    return true;
                // The FULL 64-bit result captured into a register (`mov reg64,rax`) — void has no result, an int/bool
                // result is the 32-bit eax and a float is in xmm0, so a full-rax capture means the value is a 64-bit
                // reference (it is then saved / passed as an argument / used as `this`). eax/al captures use 32/8-bit
                // moves, so requiring the exact 64-bit rax source keeps genuine int/bool results from being flagged.
                if (u.Mnemonic == Mnemonic.Mov && u.Op0Kind == OpKind.Register && u.Op1Register == Register.RAX
                    && u.Op0Register != Register.RAX
                    && u.Op0Register == u.Op0Register.GetFullRegister() && RegisterFlowUtil.GpIndex(u.Op0Register) >= 0)
                    return true;
            }
            else if (raxLive && isRef)
            {
                // Reference return whose offset-0 is loaded as a float — impossible for a real object (slot 0 = klass ptr).
                if ((u.Mnemonic is Mnemonic.Movss or Mnemonic.Movsd) && u.Op1Kind == OpKind.Memory
                    && u.MemoryBase.GetFullRegister() == Register.RAX && u.MemoryIndex == Register.None
                    && u.MemoryDisplacement64 == 0)
                    return true;
            }
            // xmm0 holds the return ONLY for a float-returning method; for a void/int/bool/ref name, reading xmm0 as the
            // result betrays a float-returning method mislabeled (e.g. Object.ToString/GetHashCode over a float getter).
            if (xmm0Live && (raxIntLike || isRef) && ReadsXmm0(u))
                return true;
            // Result consumed as a bool (`test al,al` / `test <b>,al`) — but the named method does NOT return bool (a
            // genuine bool return is ReturnKindBool, for which this test is idiomatic and must be ignored). Real: bool.
            if (raxLive && kind != Il2CppTypeModel.ReturnKindBool
                && u.Mnemonic == Mnemonic.Test && (u.Op0Register == Register.AL || u.Op1Register == Register.AL))
                return true;
            // Result consumed as a bare 32-bit int (`eax` read as a value: `cmp edi,eax`, `test eax,eax`, `add r,eax`, …)
            // — impossible for a struct/ref/float/void return. So the real method returns int (e.g. touchCount vs the
            // mislabeled Vector2 get_mouseScrollDelta). A struct's rax is a legit pointer, so this is its only rax signal.
            if (raxLive && eaxAsIntContradicts && ReadsEax(u))
                return true;

            if (u.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.IndirectBranch)
                break; // a call clobbers rax + xmm0; if the result wasn't consumed by now, it isn't observably here
            if (raxLive && (_clobber[j] & (1 << 0)) != 0) raxLive = false; // rax redefined
            if (xmm0Live && WritesXmm0(u)) xmm0Live = false;               // xmm0 redefined
            if (!raxLive && !xmm0Live) break;
        }
        // Void/int/bool/float result passed as the `this` of a managed instance call (`mov rcx,rax; …; call Instance.Method`)
        // — a `this` must be a valid object reference, which none of them can be. Survives rax being clobbered once it has
        // reached rcx, so it is a separate forward scan.
        return (raxIntLike || isFloat) && RaxBecomesThisOfManagedInstanceCall(callIdx);
    }

    /// <summary>xmm0 used as a source (the float return being consumed): as a source operand, or read-modified by a non-move op.</summary>
    private static bool ReadsXmm0(in Instruction u)
    {
        if (u.Op1Register == Register.XMM0 || u.Op2Register == Register.XMM0)
            return true; // `movaps xmm6,xmm0` / `movsd [m],xmm0` / `mulss xmm,xmm0`
        return u.Op0Register == Register.XMM0 && !IsXmmPureWrite(u.Mnemonic); // `addss xmm0,x` reads xmm0; `movaps xmm0,x` does not
    }

    private static bool WritesXmm0(in Instruction u) => u.Op0Register == Register.XMM0;

    /// <summary>eax used as a 32-bit value operand (`cmp edi,eax` / `test eax,eax` / `mov reg,eax` / `add reg,eax` …) — the result consumed as a bare int. Pure writes to eax and the xor/sub-zero idioms are not reads.</summary>
    private static bool ReadsEax(in Instruction u)
    {
        if ((u.Mnemonic is Mnemonic.Xor or Mnemonic.Sub) && u.Op0Register == Register.EAX && u.Op1Register == Register.EAX)
            return false; // `xor eax,eax` / `sub eax,eax` = zero the register (discards, not reads, the result)
        if (u.Op1Register == Register.EAX || u.Op2Register == Register.EAX)
            return true;
        return u.Op0Register == Register.EAX
            && u.Mnemonic is not (Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or Mnemonic.Movsxd or Mnemonic.Lea); // those write eax without reading it
    }

    /// <summary>
    /// On the MethodInfo-load form (`mov &lt;reg&gt;,[klass+slot+8]`), whether the register the trailing MethodInfo* lands in
    /// disagrees with the slot method's INTEGER parameter count — proving the slot is mis-mapped. For a register-returned
    /// method (rcx=this), integer params fill rdx/r8/r9 and the MethodInfo trails them, so rdx⇒0, r8⇒1, r9⇒2, anywhere
    /// else (spilled) ⇒ ≥3 integer args. Float/double params go to xmm and don't shift the sequence, so comparing against
    /// the integer-only param count is exact (no float-arg false positives). Skips struct returns (rcx becomes the hidden
    /// buffer, shifting the whole sequence). Catches both "0-param name called with args" and "N-param name called 0-arg"
    /// (e.g. PropertyInfo.GetGetMethod(bool) dispatched with the MethodInfo in rdx = 0 args → really GetIndexParameters).
    /// </summary>
    private bool ArgCountContradicts(TypeAnalysisContext type, int disp, in Instruction insn)
    {
        if ((disp & 0xF) != 8 || insn.Op0Kind != OpKind.Register)
            return false; // only the MethodInfo-load pins the register
        if (_model.GetVirtualReturnKind(type, disp) is Il2CppTypeModel.ReturnKindStruct or Il2CppTypeModel.ReturnKindUnresolved)
            return false; // struct return adds a hidden buffer pointer at position 0, shifting every arg +1 — ambiguous
        int total = _model.GetVirtualParamCount(type, disp);
        if (total < 0)
            return false;
        // MSVC x64 is POSITIONAL: the Nth argument occupies the Nth slot regardless of type — an integer arg takes
        // rcx/rdx/r8/r9, a float takes xmm0-3 AT THE SAME POSITION. For an instance method rcx=this (position 0), the P
        // declared params fill positions 1..P (each param — int, float, struct-by-val, struct-by-ptr — is exactly one
        // position), and the trailing MethodInfo* lands at position P+1, in the INTEGER register for that position:
        // P=0⇒rdx, P=1⇒r8, P=2⇒r9. So the register the MethodInfo is loaded into pins TOTAL param count (floats
        // included — counting only integer params would mis-predict any float-carrying signature and retract a correct
        // name). Only a direct load into an arg register is trusted; a load into a scratch register (rax/r10/…), used
        // when the MethodInfo is staged before a stack spill for P≥3, leaves the position unknowable ⇒ no inference.
        int implied = insn.Op0Register switch
        {
            Register.RDX => 0,
            Register.R8 => 1,
            Register.R9 => 2,
            _ => -1,
        };
        if (implied < 0)
            return false;
        return implied != total;
    }

    /// <summary>
    /// For a tail-jmp forwarder (`_method` whose whole body is a tail dispatch through a vtable slot), whether the slot
    /// resolved to a method whose signature disagrees with <see cref="_method"/>'s — a pure forwarder forwards its own
    /// args to a same-signature method, so a parameter-count or return-kind difference means the slot is mis-mapped.
    /// Only param count and return KIND are compared (covariant overrides keep the same kind; explicit-interface
    /// forwarders keep the same arity — neither is flagged), and a mismatch only ever demotes to the honest class[].
    /// </summary>
    private bool IsForwarderSlotMismatch(TypeAnalysisContext receiverType, int disp)
    {
        if (_method == null)
            return false;
        int slotParams = _model.GetVirtualParamCount(receiverType, disp);
        int enclosingParams = _method.Parameters?.Count ?? -1;
        if (slotParams >= 0 && enclosingParams >= 0 && slotParams != enclosingParams)
            return true;
        byte slotKind = _model.GetVirtualReturnKind(receiverType, disp);
        byte enclosingKind = _method.ReturnType != null ? _model.ClassifyReturnKind(_method.ReturnType) : Il2CppTypeModel.ReturnKindUnresolved;
        return slotKind != Il2CppTypeModel.ReturnKindUnresolved && enclosingKind != Il2CppTypeModel.ReturnKindUnresolved
            && slotKind != enclosingKind;
    }

    /// <summary>A GlobalKey naming one of the four System.Object base virtuals (Equals/GetHashCode/ToString/Finalize) — the inherited names sitting at vtable slots 0-3.</summary>
    private static bool IsObjectBaseMethodName(string name)
    {
        if (name == null)
            return false;
        string n = name.StartsWith("System.") ? name.Substring(7) : name; // GlobalKey may qualify with the namespace
        return n == "Object.ToString()" || n == "Object.GetHashCode()" || n == "Object.Finalize()" || n.StartsWith("Object.Equals(");
    }

    /// <summary>
    /// True only when <paramref name="a"/> and <paramref name="b"/> are CONCRETE reference classes with no inheritance
    /// relation (neither assignable to the other) and neither is System.Object — so a value of one cannot be stored into
    /// a field of the other. Conservative: interfaces / arrays / generics / value types / Object all return false (a ref
    /// legitimately fits an interface or Object field, and wrapped types are not reliably comparable), so this never
    /// condemns a valid store.
    /// </summary>
    private static bool AreUnrelatedRefClasses(TypeAnalysisContext a, TypeAnalysisContext b)
    {
        if (a == null || b == null || a.Definition == null || b.Definition == null)
            return false;
        try
        {
            if (a.IsValueType || b.IsValueType || a.IsInterface || b.IsInterface)
                return false;
        }
        catch { return false; }
        if (a.FullName == "System.Object" || b.FullName == "System.Object")
            return false;
        return !IsSameOrBase(a, b) && !IsSameOrBase(b, a);
    }

    /// <summary>Is <paramref name="baseCandidate"/> the same class as, or a base class of, <paramref name="derived"/>?</summary>
    private static bool IsSameOrBase(TypeAnalysisContext baseCandidate, TypeAnalysisContext derived)
    {
        for (TypeAnalysisContext t = derived; t != null;)
        {
            if (ReferenceEquals(t.Definition, baseCandidate.Definition)
                || (t.FullName != null && t.FullName == baseCandidate.FullName))
                return true;
            try { t = t.BaseType; }
            catch { return false; }
        }
        return false;
    }

    private static bool IsXmmPureWrite(Mnemonic m)
        => m is Mnemonic.Movss or Mnemonic.Movsd or Mnemonic.Movaps or Mnemonic.Movups
            or Mnemonic.Movd or Mnemonic.Movq or Mnemonic.Movdqa or Mnemonic.Movdqu;

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
                allocResult = TrackedValue.RefFromVtable(state[calleeReg].Type, state[calleeReg].OriginTypeName, state[calleeReg].OriginSlot);
            }
        }

        // Direct vtable dispatch `call [klass+disp]` (IndirectCall) -> rax = the slot's (reference) return type, tagged with
        // its origin slot. Computed BEFORE the clobber loop wipes the klass base register.
        bool hasDirectVtableRef = false;
        TrackedValue directVtableRef = TrackedValue.Unknown;
        if (insn.FlowControl == FlowControl.IndirectCall && insn.Op0Kind == OpKind.Memory && insn.MemoryIndex == Register.None)
        {
            int klassBase = RegisterFlowUtil.GpIndex(insn.MemoryBase);
            if (klassBase >= 0 && state[klassBase].Kind == TrackedKind.Klass && state[klassBase].Type != null
                && !IsCondemnedSlot(state[klassBase].Type, (int)insn.MemoryDisplacement64)
                && _model.TryGetVirtualReturnType(state[klassBase].Type, (int)insn.MemoryDisplacement64, out TypeAnalysisContext dvret))
            {
                hasDirectVtableRef = true;
                directVtableRef = TrackedValue.RefFromVtable(dvret, state[klassBase].Type.Name, SlotKey((int)insn.MemoryDisplacement64));
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
        if (hasDirectVtableRef)
        {
            state[0] = directVtableRef; // rax = direct vtable dispatch's reference result
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
        // One native address can be shared by MANY methods — il2cpp folds identical code, most often the
        // reference-shared instantiations of one generic method (e.g. Dictionary<K,V>.get_Item, whose closed forms
        // all compile to a single body). methods[0] is an ARBITRARY representative, so its return type may belong to
        // an unrelated instantiation; trusting it poisons every downstream field access on the result — a
        // TMP_Character read gets annotated with Mirror.NetworkBehaviour's field layout (?.syncVarDirtyBits), even a
        // float store into a purported IntPtr. Only assert the return type when every method at the address agrees on
        // it; otherwise the concrete type is unrecoverable from the address alone, so stay Unknown (a missing
        // annotation, never a fabricated one — 错标比漏标更糟).
        for (int i = 1; i < methods.Count; i++)
            if (!SameType(methods[i].ReturnType, returnType))
                return TrackedValue.Unknown;
        return TrackedValue.Ref(returnType, null);
    }

    /// <summary>
    /// Whether the vtable slot at <c>[klass + disp]</c> has been proven mis-mapped (its metadata name/return type disagrees
    /// with the runtime dispatch), by type-slot or by inherited-method key. A condemned slot's return type is unreliable, so
    /// the flow must not tag a result register with it — same predicate the arrow gate uses to demote the call to class[].
    /// </summary>
    private bool IsCondemnedSlot(TypeAnalysisContext klassType, int disp)
    {
        if (_model.CondemnedVtableSlots.Contains((klassType.Name, SlotKey(disp))))
            return true;
        return _model.TryGetVirtualMethodName(klassType, disp, out string virtualMethod)
            && _model.CondemnedVtableMethods.Contains((virtualMethod, SlotKey(disp)));
    }

    /// <summary>Semantic type identity: interned <c>Definition</c> when either has one, else <c>FullName</c> (the only stable key for wrapped array/generic/pointer types, which are re-created per resolution).</summary>
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
            // Loading a virtual function pointer from the vtable: remember its (reference) return type + originating slot for the following `call`.
            // Skip a CONDEMNED slot — its metadata name/return type is proven mis-mapped, so propagating that return type would
            // poison the result register (e.g. a BaseInput getter slot condemned to class[0x310], whose stale ref return then
            // mislabels the callee's real Vector result as UnityEngine.Object.m_CachedPtr).
            case TrackedKind.Klass when !isLea && !IsCondemnedSlot(baseValue.Type, disp) && _model.TryGetVirtualReturnType(baseValue.Type, disp, out TypeAnalysisContext vret):
                return TrackedValue.Callee(vret, baseValue.Type.Name, SlotKey(disp));
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
                // Type-mismatch condemnation: a reference produced by a vtable call (rax carries its origin slot) stored
                // into a field of an UNRELATED reference type — the returned type cannot be assigned there, so the slot's
                // name is wrong (a ToString/getter mislabel over a type-specific getter). Condemn the originating slot.
                if (isWrite && insn.Op1Register == Register.RAX && state[0].Kind == TrackedKind.ManagedRef
                    && state[0].OriginTypeName != null
                    && AreUnrelatedRefClasses(state[0].Type, field.FieldType))
                    _model.CondemnedVtableSlots.Add((state[0].OriginTypeName, state[0].OriginSlot));
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
            // Also condemn here from an arg-count contradiction on the MethodInfo-load form (`mov <reg>,[klass+slot+8]`):
            // the register the trailing MethodInfo* lands in pins the slot method's total (positional) parameter count,
            // so a mismatch with the named method's arity proves the slot is mis-mapped (see ArgCountContradicts).
            if (_model.CondemnedVtableSlots.Contains((baseValue.Type.Name, SlotKey(disp)))
                || _model.CondemnedVtableMethods.Contains((virtualMethod, SlotKey(disp))) // this inherited method proven mis-mapped on another receiver
                || ArgCountContradicts(baseValue.Type, disp, insn)) // MethodInfo register disagrees with the slot method's total param count
            {
                _model.CondemnedVtableSlots.Add((baseValue.Type.Name, SlotKey(disp)));
                _model.CondemnedVtableMethods.Add((virtualMethod, SlotKey(disp)));
                access = baseValue.Type.Name + "::class[0x" + disp.ToString("X") + "]";
            }
            else if (insn.FlowControl == FlowControl.IndirectBranch && IsForwarderSlotMismatch(baseValue.Type, disp))
            {
                // Pure pass-through wrapper (`public override X Foo(...) => inner.Foo(...)`, compiled as a tail-jmp through
                // the inner object's vtable): the forwarded method has the SAME signature as this enclosing method, so if
                // the slot resolved to a method whose parameter count or return kind differs from _method's, the slot is
                // mis-mapped (metadata-vs-runtime divergence on a System.Reflection-style vtable, e.g. FieldInfo.SetValue
                // /5 params shown as get_IsPrivate/0). Condemn app-wide + by method (propagates to non-wrapper consumers).
                _model.CondemnedVtableSlots.Add((baseValue.Type.Name, SlotKey(disp)));
                _model.CondemnedVtableMethods.Add((virtualMethod, SlotKey(disp)));
                access = baseValue.Type.Name + "::class[0x" + disp.ToString("X") + "]";
            }
            else if (IsObjectBaseMethodName(virtualMethod) && baseValue.Type.FullName != "System.Object")
            {
                // A System.Object base-slot name (Equals/GetHashCode/ToString/Finalize) resolved through a DERIVED type's
                // vtable is an unreliable placeholder: the metadata VTable inherits the Object name at slots 0-3, but the
                // runtime memory vtable at that byte offset is frequently a type-specific virtual (metadata-vs-runtime
                // divergence, shown pervasive by adversarial audit). We cannot confirm the slot really is the inherited
                // Object method, so emit the honest T::class[0xNN] fallback instead of a confident -> Object.X() guess
                // (错标比漏标更糟). Kept only when the receiver genuinely IS System.Object (a boxed value), where 0-3 hold.
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
                            _model.GetVirtualReturnKind(baseValue.Type, disp), virtualMethod));
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
