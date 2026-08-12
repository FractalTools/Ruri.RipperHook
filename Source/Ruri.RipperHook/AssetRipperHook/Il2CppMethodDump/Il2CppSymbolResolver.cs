extern alias icedreal;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using icedreal::Iced.Intel;

namespace Ruri.RipperHook.AR;

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
