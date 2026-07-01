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

    public static string TryGetName(ApplicationAnalysisContext app, ulong address)
    {
        if (!ReferenceEquals(_app, app))
        {
            _app = app;
            _cache.Clear();
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
                if (insn.Mnemonic == Mnemonic.Call && insn.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32
                    && Il2CppAsmAnnotator.IsAllocOrRaiseFunction(app, insn.NearBranchTarget))
                    throwsLike = true;
                if (insn.Mnemonic == Mnemonic.Int3)
                    throwsLike = true;
                if (insn.Mnemonic == Mnemonic.Jmp && insn.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32)
                {
                    ulong jt = insn.NearBranchTarget;
                    if (jt < address || jt >= end) { tailCalls = true; break; } // leaves the body
                }
                if (insn.Mnemonic is Mnemonic.Ret or Mnemonic.Int3)
                    break;
            }

            // Name only when the type is unmistakably an exception AND the body actually disposes of it (allocates/raises
            // inline, or tail-calls the shared constructor). Both together make the label grounded, never guessed.
            if (typeName != null && (throwsLike || tailCalls))
                return "il2cpp_throw_" + typeName;
        }
        catch { }
        return null;
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
