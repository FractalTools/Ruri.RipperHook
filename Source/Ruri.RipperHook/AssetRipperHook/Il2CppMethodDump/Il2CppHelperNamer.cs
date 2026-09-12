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
    private static readonly Dictionary<ulong, bool> _reachesRaise = new();
    private static ulong _raiseA;    private static ulong _raiseB;
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
        _cache[address] = name;
        return name;
    }

    private static string Analyze(ApplicationAnalysisContext app, ulong address)
    {
        Il2CppBinary binary = LibCpp2IlMain.Binary;
        if (binary == null || binary.is32Bit)
            return null;
        try
        {
            long raw = binary.MapVirtualAddressToRaw(address, false);
            if (raw < 0)
                return null;
            byte[] code = binary.ReadByteArrayAtRawAddress(raw, 96);
            if (code == null || code.Length == 0)
                return null;

            Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(code), address);
            ulong end = address + (ulong)code.Length;
            string typeName = null;            bool throwsLike = false;            bool tailCalls = false;            bool sawInt3 = false;            bool sawCondBranch = false;            bool reachesRaiseDirect = false;            List<ulong> callTargets = null;            int guard = 0;
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
                    sawCondBranch = true;                if (insn.Mnemonic == Mnemonic.Jmp && insn.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32)
                {
                    ulong jt = insn.NearBranchTarget;
                    if (jt < address || jt >= end) { tailCalls = true; break; }                }
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
        }
        catch { }
        return null;
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
        if (_reachesRaise.TryGetValue(addr, out bool cached))
            return cached;
        _reachesRaise[addr] = false;        bool result = false;
        try
        {
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
        }
        catch { }
        _reachesRaise[addr] = result;
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
        long raw;
        try
        {
            if (!binary.TryMapVirtualAddressToRaw(virtualAddress, out raw))
                return null;
        }
        catch { return null; }
        if (raw < 0 || raw >= binary.RawLength)
            return null;
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < 96; i++)
        {
            if (raw + i >= binary.RawLength)
                return null;
            byte c;
            try { c = binary.GetByteAtRawAddress((ulong)(raw + i)); }
            catch { return null; }
            if (c == 0)
                break;
            if (c < 0x20 || c > 0x7E)
                return null;
            sb.Append((char)c);
        }
        return sb.Length >= 2 ? sb.ToString() : null;
    }
}
