extern alias icedreal;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using icedreal::Iced.Intel;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Names the small il2cpp codegen exception-thrower helpers that the compiler emits (not in global-metadata, so they
/// otherwise stay <c>sub_XXXX</c>). Each such helper loads its exception's type name as a C-string and calls the il2cpp
/// object/exception machinery before an <c>int3</c> (noreturn) — e.g. <c>lea r8,["IndexOutOfRangeException"]; …;
/// call il2cpp_vm_object_new; …; int3</c>. We disassemble the target once (following a single leading thunk), read the
/// embedded type identifier, and — only when the body also invokes a known il2cpp allocation/raise function OR ends in
/// <c>int3</c> — name it <c>il2cpp_throw_&lt;Type&gt;</c>. Grounded in the helper's own bytes; never guessed. Result cached.
/// </summary>
internal static class Il2CppHelperNamer
{
    private static ApplicationAnalysisContext _app;
    private static readonly Dictionary<ulong, string> _cache = new();
    private static readonly Dictionary<ulong, bool> _reachesRaise = new();
    private static ulong _raiseA;   // il2cpp_vm_exception_raise
    private static ulong _raiseB;   // il2cpp_raise_exception

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
            string typeName = null;   // bare "<Name>Exception"/"<Name>Error" identifier the helper looks its class up by
            bool throwsLike = false;  // calls il2cpp object_new / raise, or ends in int3 (noreturn)
            bool tailCalls = false;   // terminal jmp leaving this body -> delegates the throw to a shared constructor
            bool sawInt3 = false;     // body reaches an int3 (a never-returns terminator)
            bool sawCondBranch = false; // a conditional branch => the body has a non-raising fall-through path (a get-or-throw, not a raise intrinsic)
            bool reachesRaiseDirect = false; // this body itself calls il2cpp_(vm_)exception_raise
            List<ulong> callTargets = null;  // near call/jmp targets, for transitive raise reachability
            int guard = 0;
            while (decoder.IP < end && guard++ < 40)
            {
                decoder.Decode(out Instruction insn);
                if (insn.IsInvalid)
                    break;

                // Exception type identifier loaded as a C-string (lea/mov reg,[Cstring]); the codegen thrower looks up
                // its Il2CppClass by this name. Only bare "…Exception"/"…Error" identifiers qualify (see IsExceptionTypeName).
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
                    sawCondBranch = true; // keep scanning (typeName/raise may still appear), but this disqualifies noReturn
                if (insn.Mnemonic == Mnemonic.Jmp && insn.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32)
                {
                    ulong jt = insn.NearBranchTarget;
                    if (jt < address || jt >= end) { tailCalls = true; break; } // leaves the body
                }
                if (insn.Mnemonic == Mnemonic.Ret)
                    break;
                if (insn.Mnemonic == Mnemonic.Int3)
                {
                    sawInt3 = true;
                    break;
                }
            }

            // Name only when the type is unmistakably an exception AND the body actually disposes of it (allocates/raises
            // inline, or tail-calls the shared constructor). Both together make the label grounded, never guessed.
            if (typeName != null && (throwsLike || tailCalls))
                return "il2cpp_throw_" + typeName;

            // Codegen raise/throw intrinsic (no embedded type to name): a STRAIGHT-LINE noreturn helper (reaches int3 with
            // no conditional branch — so no non-raising fall-through, unlike a get-or-throw) that reaches the il2cpp
            // exception-raise machinery, directly or via the construct-and-raise chain it trampolines into. Grounded (it
            // demonstrably raises and never returns) without fabricating which exception type.
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

    /// <summary>
    /// Does the code at <paramref name="addr"/> transitively reach <c>il2cpp_(vm_)exception_raise</c> within
    /// <paramref name="depth"/> call hops (following near call/jmp targets)? Bounded, memoized, cycle-guarded — used only
    /// to confirm a noreturn helper is a raise intrinsic. Reachability alone is not enough to name (a normal method may
    /// have a throw path); the caller additionally requires the top-level helper to be noreturn.
    /// </summary>
    private static bool ReachesRaise(Il2CppBinary binary, ulong addr, int depth)
    {
        if (depth <= 0)
            return false;
        if (_reachesRaise.TryGetValue(addr, out bool cached))
            return cached;
        _reachesRaise[addr] = false; // cycle guard (assume no until proven)
        bool result = false;
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

    /// <summary>
    /// A bare C# exception type identifier — PascalCase, only identifier chars (no spaces/punctuation, so messages are
    /// excluded), ending in <c>Exception</c> or <c>Error</c>. Restricting to these suffixes is what makes string-based
    /// helper naming safe: only the codegen throwers embed such a name, so there is nothing else to confuse it with.
    /// </summary>
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
