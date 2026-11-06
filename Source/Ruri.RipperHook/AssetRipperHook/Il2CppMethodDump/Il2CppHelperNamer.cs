extern alias icedreal;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using icedreal::Iced.Intel;

namespace Ruri.RipperHook.AR;

internal static class Il2CppHelperNamer
{
    private static ApplicationAnalysisContext _app;
    private static readonly Dictionary<ulong, string> _cache = new();
    private static readonly Dictionary<ulong, (bool Reaches, int Depth)> _reachesRaise = new();
    private static ulong _raiseA;
    private static ulong _raiseB;
    public static string TryGetName(ApplicationAnalysisContext app, ulong address)
    {
        if (!ReferenceEquals(_app, app))
        {
            _app = app;
            _cache.Clear();
            _reachesRaise.Clear();
            _raiseA = Il2CppAsmAnnotator.KeyFunctionAddress(app, "exception_raise");
            _raiseB = Il2CppAsmAnnotator.KeyFunctionAddress(app, "raise_exception");
        }
        if (_cache.TryGetValue(address, out string cached))
            return cached;
        string name = Analyze(app, address);
        if (name != null) name = "inferred_" + name + "_at_" + address.ToString("X");
        _cache[address] = name;
        return name;
    }

    private static string Analyze(ApplicationAnalysisContext app, ulong address)
    {
        Il2CppBinary binary = app.Binary;
        if (binary.is32Bit)
            return null;
        long raw = binary.MapVirtualAddressToRaw(address, false);
        if (raw < 0)
            return null;
        byte[] code = binary.ReadByteArrayAtRawAddress(raw, 96);
        if (code == null || code.Length == 0)
            return null;

        if (NameByShape(app, code, address) is { } shaped)
            return shaped;

        Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(code), address);
        ulong end = address + (ulong)code.Length;
        string typeName = null;
        bool throwsLike = false;
        bool tailCalls = false;
        bool sawInt3 = false;
        bool sawCondBranch = false;
        bool reachesRaiseDirect = false;
        List<ulong> callTargets = null;
        int guard = 0;
        while (decoder.IP < end && guard++ < 40)
        {
            decoder.Decode(out Instruction insn);
            if (insn.IsInvalid)
                break;

            if (typeName == null && insn.Op1Kind == OpKind.Memory
                && (insn.IsIPRelativeMemoryOperand || insn.MemoryBase == Register.None) && insn.MemoryIndex == Register.None)
            {
                string s = ReadCString(binary, insn.MemoryDisplacement64);
                if (IsExceptionTypeName(s))
                    typeName = s;
            }
            if ((insn.Mnemonic == Mnemonic.Call || insn.Mnemonic == Mnemonic.Jmp)
                && insn.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32)
            {
                ulong t = insn.NearBranchTarget;
                if (IsRaise(t))
                    reachesRaiseDirect = true;
                else if (t < address || t >= end)
                    (callTargets ??= new()).Add(t);
            }
            if (insn.Mnemonic == Mnemonic.Call && insn.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32
                && Il2CppAsmAnnotator.IsAllocOrRaiseFunction(app, insn.NearBranchTarget))
                throwsLike = true;
            if (insn.Mnemonic == Mnemonic.Int3)
                throwsLike = true;
            if (insn.FlowControl == FlowControl.ConditionalBranch)
                sawCondBranch = true;
            if (insn.Mnemonic == Mnemonic.Jmp && insn.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32)
            {
                ulong jt = insn.NearBranchTarget;
                if (jt < address || jt >= end) { tailCalls = true; break; }
            }
            if (insn.Mnemonic == Mnemonic.Ret)
                break;
            if (insn.Mnemonic == Mnemonic.Int3)
            {
                sawInt3 = true;
                break;
            }
        }

        if (typeName != null && (throwsLike || tailCalls))
            return "il2cpp_throw_" + typeName;

        if (sawInt3 && !sawCondBranch && (reachesRaiseDirect || AnyReachesRaise(binary, callTargets)))
            return "il2cpp_codegen_raise";
        return null;
    }

    /// <summary>The two guards the compiler emits ahead of nearly every use of an object, outlined into functions of
    /// their own: the class initialiser's flag test and the null test. Each tests its argument and sends the failing
    /// case to a block that does one thing -- run the initialiser, or call something that never returns.</summary>
    private static string NameByShape(ApplicationAnalysisContext app, byte[] code, ulong address)
    {
        Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(code), address);
        List<Instruction> head = new(4);
        while (head.Count < 4)
        {
            decoder.Decode(out Instruction insn);
            if (insn.IsInvalid)
                break;
            head.Add(insn);
        }
        int at = head.Count > 0 && head[0].Mnemonic == Mnemonic.Sub && head[0].Op0Register == Register.RSP ? 1 : 0;
        if (head.Count <= at + 1)
            return null;
        Instruction test = head[at];
        Instruction branch = head[at + 1];
        if (branch.Mnemonic != Mnemonic.Je || branch.Op0Kind != OpKind.NearBranch64)
            return null;
        bool comparesFlag = test.Mnemonic == Mnemonic.Cmp && test.Op0Kind == OpKind.Memory
            && test.MemoryBase == Register.RCX && test.MemoryIndex == Register.None
            && test.Op1Kind is OpKind.Immediate8 or OpKind.Immediate8to32 or OpKind.Immediate8to64 && test.GetImmediate(1) == 0;
        bool testsPointer = test.Mnemonic == Mnemonic.Test && test.Op0Register == Register.RCX && test.Op1Register == Register.RCX;
        if (comparesFlag && FailureBlock(app, branch.NearBranchTarget, classInit: true))
            return "il2cpp_class_init_check";
        if (testsPointer && FailureBlock(app, branch.NearBranchTarget, classInit: false))
            return "il2cpp_null_check";
        return null;
    }

    private static bool FailureBlock(ApplicationAnalysisContext app, ulong target, bool classInit)
    {
        Il2CppBinary binary = app.Binary;
        long raw = binary.MapVirtualAddressToRaw(target, false);
        if (raw < 0)
            return false;
        byte[] bytes = binary.ReadByteArrayAtRawAddress(raw, 16);
        Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), target);
        decoder.Decode(out Instruction first);
        decoder.Decode(out Instruction second);
        if (first.Mnemonic is not (Mnemonic.Call or Mnemonic.Jmp) || first.Op0Kind != OpKind.NearBranch64)
            return false;
        return classInit
            ? Il2CppAsmAnnotator.IsKeyFunction(app, first.NearBranchTarget, "class_init")
            : second.Mnemonic == Mnemonic.Int3;
    }

    private static bool IsRaise(ulong target)
        => (_raiseA != 0 && target == _raiseA) || (_raiseB != 0 && target == _raiseB);

    private static bool AnyReachesRaise(Il2CppBinary binary, List<ulong> targets)
    {
        if (targets == null)
            return false;
        foreach (ulong t in targets)
            if (ReachesRaise(binary, t, 4))
                return true;
        return false;
    }

    private static bool ReachesRaise(Il2CppBinary binary, ulong addr, int depth)
    {
        if (depth <= 0)
            return false;
        if (_reachesRaise.TryGetValue(addr, out (bool Reaches, int Depth) cached) && (cached.Reaches || cached.Depth >= depth))
            return cached.Reaches;
        _reachesRaise[addr] = (false, 0);
        bool result = false;
        long raw = binary.MapVirtualAddressToRaw(addr, false);
        if (raw >= 0)
        {
            byte[] code = binary.ReadByteArrayAtRawAddress(raw, 96);
            if (code != null && code.Length > 0)
            {
                Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(code), addr);
                ulong end = addr + (ulong)code.Length;
                int guard = 0;
                while (decoder.IP < end && guard++ < 40)
                {
                    decoder.Decode(out Instruction insn);
                    if (insn.IsInvalid)
                        break;
                    if ((insn.Mnemonic == Mnemonic.Call || insn.Mnemonic == Mnemonic.Jmp)
                        && insn.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32)
                    {
                        ulong t = insn.NearBranchTarget;
                        if (IsRaise(t) || ReachesRaise(binary, t, depth - 1)) { result = true; break; }
                    }
                    if (insn.Mnemonic is Mnemonic.Ret or Mnemonic.Int3)
                        break;
                }
            }
        }
        _reachesRaise[addr] = (result, depth);
        return result;
    }

    private static bool IsExceptionTypeName(string s)
    {
        if (s == null || s.Length < 6 || s.Length > 64)
            return false;
        if (s[0] < 'A' || s[0] > 'Z')
            return false;
        foreach (char c in s)
        {
            bool ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_';
            if (!ok)
                return false;
        }
        return s.EndsWith("Exception") || s.EndsWith("Error");
    }

    private static string ReadCString(Il2CppBinary binary, ulong virtualAddress)
    {
        if (!binary.TryMapVirtualAddressToRaw(virtualAddress, out long raw))
            return null;
        if (raw < 0 || raw >= binary.RawLength)
            return null;
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < 96; i++)
        {
            if (raw + i >= binary.RawLength)
                return null;
            byte c = binary.GetByteAtRawAddress((ulong)(raw + i));
            if (c == 0)
                break;
            if (c < 0x20 || c > 0x7E)
                return null;
            sb.Append((char)c);
        }
        return sb.Length >= 2 ? sb.ToString() : null;
    }
}
