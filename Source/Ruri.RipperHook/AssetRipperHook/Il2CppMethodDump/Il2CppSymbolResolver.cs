extern alias icedreal;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using icedreal::Iced.Intel;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Instruction-aware symbol resolver handed to Iced's <see cref="MasmFormatter"/>. Because Iced tells us the exact
/// operand kind, this replaces the old regex-over-formatted-text pass and fixes its core flaw: it can no longer turn
/// an <b>immediate</b> (e.g. a GetHashCode seed <c>add eax,5E593F7Ah</c>) into a bogus <c>sub_</c> code label.
/// <list type="bullet">
/// <item>Branch / call target → managed method name / PE export / il2cpp key function / <c>sub_</c> / <c>loc_</c>.</item>
/// <item>Absolute data global (RIP-relative or bare <c>[disp]</c>) → string literal / TypeInfo / method / field /
/// constant-pool value / <c>g_</c> — all via <see cref="Il2CppAsmAnnotator.ResolveAddress"/>.</item>
/// <item>Register-relative displacement (<c>[rcx+18h]</c>) → left raw here; it is a field/struct offset and is
/// recovered as a trailing <c>; this.field</c> comment by <see cref="Il2CppRegisterFlow"/>.</item>
/// <item>Immediate / anything else → not resolved (the real value is preserved).</item>
/// </list>
/// </summary>
internal sealed class Il2CppSymbolResolver : ISymbolResolver
{
    private readonly ApplicationAnalysisContext _app;
    private readonly IReadOnlyDictionary<ulong, string> _overrides;
    private readonly IReadOnlyDictionary<ulong, Il2CppAsmAnnotator.DataConstantOperand> _dataConstants;

    public Il2CppSymbolResolver(ApplicationAnalysisContext app, IReadOnlyDictionary<ulong, string> overrides,
        IReadOnlyDictionary<ulong, Il2CppAsmAnnotator.DataConstantOperand> dataConstants)
    {
        _app = app;
        _overrides = overrides;
        _dataConstants = dataConstants;
    }

    public bool TryGetSymbol(in Instruction instruction, int operand, int instructionOperand, ulong address, int addressSize, out SymbolResult symbol)
    {
        symbol = default;
        if (instructionOperand < 0)
            return false;

        OpKind kind = instruction.GetOpKind(instructionOperand);

        if (kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
        {
            string branch = Il2CppAsmAnnotator.ResolveAddress(_app, address, inBrackets: false, _overrides, _dataConstants);
            if (branch == null) return false;
            symbol = new SymbolResult(address, branch);
            return true;
        }

        if (kind == OpKind.Memory)
        {
            // Only ABSOLUTE data references get a global symbol. A register-relative displacement is a field/struct
            // offset (address is the small displacement, not a VA) and is handled by the register-flow comment pass.
            bool absolute = instruction.IsIPRelativeMemoryOperand
                || (instruction.MemoryBase == Register.None && instruction.MemoryIndex == Register.None);
            if (!absolute)
                return false;

            string global = Il2CppAsmAnnotator.ResolveAddress(_app, address, inBrackets: true, _overrides, _dataConstants);
            if (global == null) return false;
            symbol = new SymbolResult(address, global);
            return true;
        }

        return false;
    }
}
