extern alias icedreal;
using System;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.PE;
using icedreal::Iced.Intel;

namespace Ruri.RipperHook.AR;

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

internal readonly struct TrackedValue : IEquatable<TrackedValue>
{
    public readonly TrackedKind Kind;
    public readonly TypeAnalysisContext Type;
    public readonly string Alias;
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
    public static TrackedValue KlassOf(TypeAnalysisContext type) => new(TrackedKind.Klass, type, null);    public static TrackedValue Callee(TypeAnalysisContext returnType, string originTypeName, int originSlot) => new(TrackedKind.Callee, returnType, null, originTypeName, originSlot);
    public bool IsKnown => Kind != TrackedKind.Unknown;
    public bool Equals(TrackedValue other) => Kind == other.Kind && SameType(Type, other.Type);
    public override bool Equals(object obj) => obj is TrackedValue value && Equals(value);
    public override int GetHashCode() => (int)Kind * 397;
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

internal sealed class Il2CppRegisterFlow
{
    private const int InstructionBudget = 12000;

    private readonly ApplicationAnalysisContext _app;
    private readonly MethodAnalysisContext _method;
    private readonly List<Instruction> _instructions;
    private readonly Il2CppTypeModel _model;
    private readonly bool _isPe;
    private readonly int _staticFieldsOffset;
    private readonly ulong _objectNewAddress;
    private readonly Dictionary<ulong, int> _indexByIp = new();
    private int[] _blockOf;
    private int[] _blockFirst;
    private List<int>[] _successors;
    private List<int>[] _predecessors;
    private TrackedValue[][] _entryState;
    private ushort[] _clobber;
    private bool[] _writesMemory;
    private string[] _comments;
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
            _comments = null;        }
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
            foreach (UsedMemory usedMemory in info.GetUsedMemory())
            {
                if (IsWrite(usedMemory.Access) && usedMemory.Base == insn.MemoryBase && usedMemory.Index == insn.MemoryIndex)
                {
                    _writesMemory[i] = true;
                    break;
                }
            }
            if (insn.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                mask |= volatileMask;
            _clobber[i] = mask;
        }
    }

    private ushort VolatileMask()
    {
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
            if (_entryState[b] == null) continue;            TransferBlock(b, _entryState[b], _comments);
        }
    }

    private void RetractInconsistentArrows()
    {
        if (_arrowRetractCandidates == null || _comments == null) return;
        HashSet<(string, int)> condemned = null;
        foreach ((int index, int fnReg, int disp, string typeName, byte kind, string methodName) in _arrowRetractCandidates)
        {
            if ((uint)index < (uint)_comments.Length && DispatchResultContradicts(index, fnReg, kind))
            {
                (condemned ??= new()).Add((typeName, SlotKey(disp)));
                _model.CondemnedVtableSlots.Add((typeName, SlotKey(disp)));                if (methodName != null)
                    _model.CondemnedVtableMethods.Add((methodName, SlotKey(disp)));            }
        }
        if (condemned == null) return;
        foreach ((int index, int _, int disp, string typeName, byte _, string _) in _arrowRetractCandidates)
        {
            if ((uint)index < (uint)_comments.Length && _comments[index] != null && _comments[index].StartsWith("-> ")
                && condemned.Contains((typeName, SlotKey(disp))))
                _comments[index] = typeName + "::class[0x" + disp.ToString("X") + "]";
        }
    }

    private static int SlotKey(int disp) => disp & ~0xF;

    private bool DispatchResultContradicts(int arrowIndex, int fnReg, byte kind)
    {
        bool raxIntLike = kind is Il2CppTypeModel.ReturnKindVoid or Il2CppTypeModel.ReturnKindScalarInt or Il2CppTypeModel.ReturnKindBool;
        bool isFloat = kind == Il2CppTypeModel.ReturnKindScalarFloat;
        bool isRef = kind == Il2CppTypeModel.ReturnKindRef;
        bool isStruct = kind == Il2CppTypeModel.ReturnKindStruct;
        bool eaxAsIntContradicts = kind is Il2CppTypeModel.ReturnKindVoid or Il2CppTypeModel.ReturnKindScalarFloat
            or Il2CppTypeModel.ReturnKindRef or Il2CppTypeModel.ReturnKindStruct;
        if (!raxIntLike && !isFloat && !isRef && !isStruct)
            return false;
        int n = _instructions.Count;
        int callIdx;
        if (fnReg < 0)
        {
            callIdx = arrowIndex;        }
        else
        {
            callIdx = -1;
            for (int j = arrowIndex + 1; j < n && j <= arrowIndex + 10; j++)
            {
                Instruction c = _instructions[j];
                if (c.FlowControl == FlowControl.IndirectCall && c.Op0Kind == OpKind.Register
                    && RegisterFlowUtil.GpIndex(c.Op0Register) == fnReg)
                { callIdx = j; break; }
                if (c.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.IndirectBranch)
                    return false;                if ((_clobber[j] & (1 << fnReg)) != 0) return false;            }
            if (callIdx < 0) return false;
        }

        bool raxLive = true, xmm0Live = true;
        for (int j = callIdx + 1; j < n && j <= callIdx + 16; j++)
        {
            Instruction u = _instructions[j];
            if (raxLive && (raxIntLike || isFloat))
            {
                if (u.MemoryBase.GetFullRegister() == Register.RAX || u.MemoryIndex.GetFullRegister() == Register.RAX)
                    return true;
                if (u.Mnemonic == Mnemonic.Mov && u.Op0Kind == OpKind.Memory
                    && u.Op1Kind == OpKind.Register && u.Op1Register == Register.RAX)
                    return true;
                if (u.Mnemonic == Mnemonic.Mov && u.Op0Kind == OpKind.Register && u.Op1Register == Register.RAX
                    && u.Op0Register != Register.RAX
                    && u.Op0Register == u.Op0Register.GetFullRegister() && RegisterFlowUtil.GpIndex(u.Op0Register) >= 0)
                    return true;
            }
            else if (raxLive && isRef)
            {
                if ((u.Mnemonic is Mnemonic.Movss or Mnemonic.Movsd) && u.Op1Kind == OpKind.Memory
                    && u.MemoryBase.GetFullRegister() == Register.RAX && u.MemoryIndex == Register.None
                    && u.MemoryDisplacement64 == 0)
                    return true;
            }
            if (xmm0Live && (raxIntLike || isRef) && ReadsXmm0(u))
                return true;
            if (raxLive && kind != Il2CppTypeModel.ReturnKindBool
                && u.Mnemonic == Mnemonic.Test && (u.Op0Register == Register.AL || u.Op1Register == Register.AL))
                return true;
            if (raxLive && eaxAsIntContradicts && ReadsEax(u))
                return true;
            if (raxLive && kind == Il2CppTypeModel.ReturnKindVoid && ReadsAl(u))
                return true;

            if (u.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.IndirectBranch)
                break;            if (raxLive && (_clobber[j] & (1 << 0)) != 0) raxLive = false;            if (xmm0Live && WritesXmm0(u)) xmm0Live = false;            if (!raxLive && !xmm0Live) break;
        }
        return (raxIntLike || isFloat) && RaxBecomesThisOfManagedInstanceCall(callIdx);
    }

    private static bool ReadsXmm0(in Instruction u)
    {
        if (u.Op1Register == Register.XMM0 || u.Op2Register == Register.XMM0)
            return true;        return u.Op0Register == Register.XMM0 && !IsXmmPureWrite(u.Mnemonic);    }

    private static bool WritesXmm0(in Instruction u) => u.Op0Register == Register.XMM0;

    private static bool ReadsAl(in Instruction u)
    {
        if ((u.Mnemonic is Mnemonic.Xor or Mnemonic.Sub) && u.Op0Register == Register.AL && u.Op1Register == Register.AL)
            return false;        if (u.Op1Register == Register.AL || u.Op2Register == Register.AL)
            return true;        return u.Op0Register == Register.AL            && u.Mnemonic is not (Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or Mnemonic.Lea);
    }

    private static bool ReadsEax(in Instruction u)
    {
        if ((u.Mnemonic is Mnemonic.Xor or Mnemonic.Sub) && u.Op0Register == Register.EAX && u.Op1Register == Register.EAX)
            return false;        if (u.Op1Register == Register.EAX || u.Op2Register == Register.EAX)
            return true;
        return u.Op0Register == Register.EAX
            && u.Mnemonic is not (Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or Mnemonic.Movsxd or Mnemonic.Lea);    }

    private bool ArgCountContradicts(TypeAnalysisContext type, int disp, in Instruction insn)
    {
        if ((disp & 0xF) != 8 || insn.Op0Kind != OpKind.Register)
            return false;        if (_model.GetVirtualReturnKind(type, disp) is Il2CppTypeModel.ReturnKindStruct or Il2CppTypeModel.ReturnKindUnresolved)
            return false;        int total = _model.GetVirtualParamCount(type, disp);
        if (total < 0)
            return false;
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

    private bool ArgTypeContradicts(TypeAnalysisContext type, int disp, in Instruction insn, TrackedValue[] state)
    {
        if (insn.FlowControl != FlowControl.IndirectCall)            return false;
        if (_model.GetVirtualReturnKind(type, disp) is Il2CppTypeModel.ReturnKindStruct or Il2CppTypeModel.ReturnKindUnresolved)
            return false;        System.ReadOnlySpan<Register> argRegisters = stackalloc Register[] { Register.RDX, Register.R8, Register.R9 };
        for (int i = 0; i < argRegisters.Length; i++)
        {
            TypeAnalysisContext paramType = _model.GetVirtualParamType(type, disp, i);
            if (paramType == null)
                continue;
            int argReg = RegisterFlowUtil.GpIndex(argRegisters[i]);
            if (argReg < 0)
                continue;
            TrackedValue arg = state[argReg];
            if (arg.Kind == TrackedKind.ManagedRef && arg.Type != null && AreUnrelatedRefClasses(arg.Type, paramType))
                return true;
        }
        return false;
    }

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

    private static bool IsObjectBaseMethodName(string name)
    {
        if (name == null)
            return false;
        string n = name.StartsWith("System.") ? name.Substring(7) : name;        return n == "Object.ToString()" || n == "Object.GetHashCode()" || n == "Object.Finalize()" || n.StartsWith("Object.Equals(");
    }

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
                if ((_clobber[j] & (1 << 0)) != 0) return false;            }
            else
            {
                if (u.FlowControl == FlowControl.Call && u.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32
                    && _app.MethodsByAddress.TryGetValue(u.NearBranchTarget, out List<MethodAnalysisContext> callee)
                    && callee.Count > 0 && !callee[0].IsStatic)
                    return true;
                if ((_clobber[j] & (1 << 1)) != 0) return false;            }
        }
        return false;
    }

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
        else if (insn.FlowControl == FlowControl.Call && insn.Op0Kind == OpKind.Register)
        {
            int calleeReg = RegisterFlowUtil.GpIndex(insn.Op0Register);
            if (calleeReg >= 0 && state[calleeReg].Kind == TrackedKind.Callee && state[calleeReg].Type != null)
            {
                isAlloc = true;
                allocResult = TrackedValue.RefFromVtable(state[calleeReg].Type, state[calleeReg].OriginTypeName, state[calleeReg].OriginSlot);
            }
        }

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

        if (insn.FlowControl == FlowControl.Call
            && insn.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64
            && state[1].Kind == TrackedKind.ManagedRef && state[1].OriginTypeName != null && state[1].Type != null
            && _app.MethodsByAddress.TryGetValue(insn.NearBranchTarget, out List<MethodAnalysisContext> receiverMethods)
            && receiverMethods.Count == 1 && !receiverMethods[0].IsStatic && receiverMethods[0].DeclaringType != null
            && AreUnrelatedRefClasses(state[1].Type, receiverMethods[0].DeclaringType))
        {
            _model.CondemnedVtableSlots.Add((state[1].OriginTypeName, state[1].OriginSlot));
        }

        ushort mask = _clobber[index];
        for (int r = 0; r < 16; r++)        {
            if ((mask & (1 << r)) != 0) state[r] = TrackedValue.Unknown;
        }

        if (insn.FlowControl == FlowControl.Call)
        {
            state[0] = isAlloc ? allocResult : CallReturn(insn);            return;
        }
        if (hasDirectVtableRef)
        {
            state[0] = directVtableRef;            return;
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
        for (int i = 1; i < methods.Count; i++)
            if (!SameType(methods[i].ReturnType, returnType))
                return TrackedValue.Unknown;
        return TrackedValue.Ref(returnType, null);
    }

    private bool IsCondemnedSlot(TypeAnalysisContext klassType, int disp)
    {
        if (_model.CondemnedVtableSlots.Contains((klassType.Name, SlotKey(disp))))
            return true;
        return _model.TryGetVirtualMethodName(klassType, disp, out string virtualMethod)
            && _model.CondemnedVtableMethods.Contains((virtualMethod, SlotKey(disp)));
    }

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
            case TrackedKind.Klass when !isLea && !IsCondemnedSlot(baseValue.Type, disp) && _model.TryGetVirtualReturnType(baseValue.Type, disp, out TypeAnalysisContext vret):
                return TrackedValue.Callee(vret, baseValue.Type.Name, SlotKey(disp));
            default:
                return TrackedValue.Unknown;
        }
    }

    private string BuildComment(in Instruction insn, int index, TrackedValue[] state)
    {
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
            if (_model.CondemnedVtableSlots.Contains((baseValue.Type.Name, SlotKey(disp)))
                || _model.CondemnedVtableMethods.Contains((virtualMethod, SlotKey(disp)))                || ArgCountContradicts(baseValue.Type, disp, insn)                || ArgTypeContradicts(baseValue.Type, disp, insn, state))            {
                _model.CondemnedVtableSlots.Add((baseValue.Type.Name, SlotKey(disp)));
                _model.CondemnedVtableMethods.Add((virtualMethod, SlotKey(disp)));
                access = baseValue.Type.Name + "::class[0x" + disp.ToString("X") + "]";
            }
            else if (insn.FlowControl == FlowControl.IndirectBranch && IsForwarderSlotMismatch(baseValue.Type, disp))
            {
                _model.CondemnedVtableSlots.Add((baseValue.Type.Name, SlotKey(disp)));
                _model.CondemnedVtableMethods.Add((virtualMethod, SlotKey(disp)));
                access = baseValue.Type.Name + "::class[0x" + disp.ToString("X") + "]";
            }
            else if (IsObjectBaseMethodName(virtualMethod) && baseValue.Type.FullName != "System.Object")
            {
                access = baseValue.Type.Name + "::class[0x" + disp.ToString("X") + "]";
            }
            else
            {
                access = "-> " + virtualMethod;                if (insn.FlowControl != FlowControl.IndirectBranch)
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
        string source = IsStoreMov(insn.Mnemonic) ? SourceToken(insn, state) : null;
        return source == null ? access : access + " = " + source;
    }

    private static bool IsStoreMov(Mnemonic mnemonic)
        => mnemonic is Mnemonic.Mov or Mnemonic.Movss or Mnemonic.Movsd or Mnemonic.Movaps or Mnemonic.Movups
            or Mnemonic.Movdqa or Mnemonic.Movdqu or Mnemonic.Movq or Mnemonic.Movd;

    private static string SourceToken(in Instruction insn, TrackedValue[] state)
    {
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
                if (reg < 0) break;                ParameterAnalysisContext par = _method.Parameters[p];
                if (IsSeedableRef(par.ParameterType)) state[reg] = TrackedValue.Ref(par.ParameterType, par.Name ?? ("arg" + p));
            }
        }
        else
        {
            int nreg = 0;            if (hiddenReturn) { state[ElfSlotReg(0)] = TrackedValue.Ref(_method.ReturnType, "retval"); nreg = 1; }
            if (addThis)
            {
                int reg = ElfSlotReg(nreg);
                if (reg >= 0 && _method.DeclaringType != null) state[reg] = TrackedValue.Ref(_method.DeclaringType, "this");
                nreg++;
            }
            foreach (ParameterAnalysisContext par in _method.Parameters)
            {
                if (IsFloat(par.ParameterType)) continue;                int reg = ElfSlotReg(nreg);
                if (reg < 0) break;
                if (IsSeedableRef(par.ParameterType)) state[reg] = TrackedValue.Ref(par.ParameterType, par.Name ?? "arg");
                nreg++;
            }
        }
        return state;
    }

    private static int MsvcSlotReg(int slot) => slot switch { 0 => 1, 1 => 2, 2 => 8, 3 => 9, _ => -1 };    private static int ElfSlotReg(int slot) => slot switch { 0 => 7, 1 => 6, 2 => 2, 3 => 1, 4 => 8, 5 => 9, _ => -1 };
    private bool IsSeedableRef(TypeAnalysisContext type)
        => type != null && !type.IsValueType;
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
