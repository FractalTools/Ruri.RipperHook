extern alias icedreal;
using System;
using System.Collections.Generic;
using System.Linq;
using AssetRipper.Import.Logging;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using icedreal::Iced.Intel;
namespace Ruri.RipperHook.AR;

internal static class Il2CppX86Listing
{
    private static ApplicationAnalysisContext _condemnationApp;
    private static readonly HashSet<AssemblyAnalysisContext> _condemnationScanned = new();

    public static Il2CppNativeBody Decode(ApplicationAnalysisContext app, MethodAnalysisContext method, bool withChunks = true) =>
        Il2CppNativeBody.Read(app, method.UnderlyingPointer, withChunks: withChunks);

    public static Il2CppNativeBody DecodeRange(ApplicationAnalysisContext app, ulong start, ulong end, bool withChunks = true) =>
        Il2CppNativeBody.Read(app, start, end, withChunks);

    private static void EnsureCondemnationScan(ApplicationAnalysisContext app, Il2CppTypeModel model, MethodAnalysisContext current)
    {
        if (!ReferenceEquals(_condemnationApp, app)) { _condemnationApp = app; _condemnationScanned.Clear(); }
        AssemblyAnalysisContext assembly = current.DeclaringType?.DeclaringAssembly;
        if (assembly == null || !_condemnationScanned.Add(assembly)) return;
        foreach (TypeAnalysisContext type in assembly.Types)
        {
            foreach (MethodAnalysisContext method in type.Methods)
            {
                if (method.UnderlyingPointer == 0) continue;
                try
                {
                    Il2CppNativeBody body = Decode(app, method);
                    if (body.Instructions.Count == 0) continue;
                    new Il2CppRegisterFlow(app, method, body, model).Analyze();
                }
                catch (Exception exception)
                {
                    Logger.Warning(LogCategory.Export,
                        $"[Il2CppMethodDump] {method.FullName} is left out of the vtable consistency scan: {Describe(exception)}");
                }
            }
        }
    }

    /// <summary>One line saying what went wrong and where it was thrown -- what the dump states about an item it
    /// has to leave out.</summary>
    internal static string Describe(Exception exception) =>
        $"{exception.GetType().Name} in {exception.TargetSite?.DeclaringType?.Name}.{exception.TargetSite?.Name}: {exception.Message}";

    public static string Render(ApplicationAnalysisContext app, MethodAnalysisContext method, bool withAddresses = false,
        bool withChunks = false) => Render(app, method, Decode(app, method, withChunks), withAddresses);

    public static string Render(ApplicationAnalysisContext app, MethodAnalysisContext method, Il2CppNativeBody body,
        bool withAddresses = true)
    {
        Il2CppTypeModel model = Il2CppTypeModel.Get(app);
        EnsureCondemnationScan(app, model, method);
        Il2CppRegisterFlow flow = new(app, method, body, model);
        flow.Analyze();
        return Format(app, body, flow, withAddresses);
    }

    /// <summary>The code between two addresses as text, with no method behind it to say what its registers hold: a
    /// runtime helper, a thunk, any function a call lands in.</summary>
    public static string RenderRange(ApplicationAnalysisContext app, ulong start, ulong end, bool withAddresses = true,
        bool withChunks = true)
        => Format(app, DecodeRange(app, start, end, withChunks), null, withAddresses);

    public static string RenderRange(ApplicationAnalysisContext app, Il2CppNativeBody body, bool withAddresses = true) =>
        Format(app, body, null, withAddresses);

    public static ulong FindEnd(ApplicationAnalysisContext app, ulong start, int limit = 0x4000) =>
        Il2CppNativeBody.Read(app, start, withChunks: false, limit: limit).PrimaryEnd;

    private static string Format(ApplicationAnalysisContext app, Il2CppNativeBody body, Il2CppRegisterFlow flow, bool withAddresses)
    {
        List<Instruction> everything = body.Instructions;
        Dictionary<ulong, int> indices = new();
        for (int index = 0; index < everything.Count; index++)
            indices[everything[index].IP] = index;
        HashSet<ulong> labels = everything.Where(instruction => instruction.FlowControl is FlowControl.ConditionalBranch
                or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch)
            .SelectMany(instruction => body.Successors.TryGetValue(instruction.IP, out List<ulong> targets) ? targets : Enumerable.Empty<ulong>())
            .Where(indices.ContainsKey).ToHashSet();
        Dictionary<ulong, string> overrides = DetectMetadataInitIdiom(app, body);
        DetectIcallCacheIdiom(app, body, ref overrides);
        Dictionary<ulong, Il2CppAsmAnnotator.DataConstantOperand> dataConstants = CollectDataConstants(everything);

        Il2CppSymbolResolver resolver = new(app, overrides, dataConstants);
        MasmFormatter formatter = new(resolver);
        formatter.Options.ShowSymbolAddress = true;
        StringOutput output = new();
        System.Text.StringBuilder sb = new(everything.Count * 32);
        sb.Append("; native ranges preserved; analysis=").Append(body.Status).Append('\n');
        foreach (Il2CppNativeBody.Issue issue in body.Issues)
            sb.Append("; analysis ").Append(issue.Address.ToString("X")).Append(' ').Append(issue.Reason).Append('\n');
        foreach (Il2CppNativeBody.SourceRange source in body.SourceRanges)
            sb.Append("; source range ").Append(source.Start.ToString("X")).Append("..").Append(source.End.ToString("X"))
                .Append(" raw=").Append(source.RawOffset.ToString("X")).Append(" length=").Append(source.End - source.Start)
                .Append(" source=").Append(source.Source).Append('\n');
        foreach (Il2CppNativeBody.DataRange view in body.DataRanges)
        {
            sb.Append("; table view site=").Append(view.SourceSite.ToString("X")).Append(' ')
                .Append(view.Start.ToString("X")).Append("..").Append(view.End.ToString("X"))
                .Append(" raw=").Append(view.RawOffset.ToString("X")).Append(" entry_size=").Append(view.EntrySize)
                .Append(" interpretation=").Append(view.Interpretation).Append('\n');
            for (int index = 0; index < view.Targets.Length; index++)
                sb.Append("; table target site=").Append(view.SourceSite.ToString("X"))
                    .Append(" entry=").Append((view.Start + (ulong)(index * view.EntrySize)).ToString("X"))
                    .Append(" -> ").Append(view.Targets[index].ToString("X")).Append('\n');
        }
        foreach (Il2CppNativeBody.Region region in body.Regions)
        {
            foreach (Il2CppNativeBody.SourceRange source in body.SourceRanges.Where(source => source.Executable
                && source.Start == region.Start && source.Start != body.Entry))
                sb.Append("; ---- chunk ").Append(source.Start.ToString("X")).Append("..").Append(source.End.ToString("X")).Append(" ----\n");
            sb.Append("; range ").Append(region.Start.ToString("X")).Append("..").Append(region.End.ToString("X"))
                .Append(" raw=").Append(region.RawOffset.ToString("X")).Append(" length=").Append(region.End - region.Start)
                .Append(" source=").Append(region.Source).Append('\n');
            foreach (Il2CppNativeBody.Partition partition in body.Partitions.Where(partition => partition.Start >= region.Start
                && partition.End <= region.End))
            {
                if (partition.Kind == Il2CppNativeBody.ContentKind.Code)
                {
                    int index = indices[partition.Start];
                    Instruction instruction = everything[index];
                    Append(sb, instruction, index, labels, formatter, output, flow, withAddresses);
                }
                else if (partition.Kind == Il2CppNativeBody.ContentKind.Data)
                {
                    Il2CppNativeBody.DataRange[] views = body.DataRanges.Where(view => partition.Start >= view.Start
                        && partition.End <= view.End).ToArray();
                    int unit = views.Select(view => view.EntrySize).Distinct().Count() == 1 ? views[0].EntrySize : 1;
                    if ((partition.End - partition.Start) % (ulong)unit != 0
                        || views.Any(view => (partition.Start - view.Start) % (ulong)unit != 0))
                        unit = 1;
                    sb.Append("; data jump_table ").Append(partition.Start.ToString("X")).Append("..").Append(partition.End.ToString("X"))
                        .Append(" raw=").Append(partition.RawOffset.ToString("X")).Append(" unit_size=").Append(unit).Append('\n');
                    for (ulong address = partition.Start; address < partition.End; address += (ulong)unit)
                        sb.Append(address.ToString("X")).Append("  db ")
                            .Append(string.Join(",", region.Bytes.Skip((int)(address - region.Start)).Take(unit).Select(value => "0" + value.ToString("X2") + "h")))
                            .Append('\n');
                }
                else
                    sb.Append("; unknown ").Append(partition.Start.ToString("X")).Append("..").Append(partition.End.ToString("X"))
                        .Append(" raw=").Append(partition.RawOffset.ToString("X")).Append(" length=").Append(partition.End - partition.Start)
                        .Append(" reason=").Append(partition.Reason).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static void Append(System.Text.StringBuilder sb, Instruction instruction, int index, HashSet<ulong> labels,
        MasmFormatter formatter, StringOutput output, Il2CppRegisterFlow flow, bool withAddresses)
    {
        if (labels.Contains(instruction.IP))
            sb.Append("loc_").Append(instruction.IP.ToString("X")).Append(":\n");
        formatter.Format(instruction, output);
        if (withAddresses)
            sb.Append(instruction.IP.ToString("X")).Append("  ");
        sb.Append(output.ToStringAndReset());
        string comment = flow?.CommentAt(index);
        if (comment != null)
            sb.Append("  ; ").Append(comment);
        sb.Append('\n');
    }

    private static Dictionary<ulong, string> DetectMetadataInitIdiom(ApplicationAnalysisContext app, Il2CppNativeBody body)
    {
        List<Instruction> instructions = body.Instructions;
        ulong initMethod = Il2CppAsmAnnotator.KeyFunctionAddress(app, "initialize_method");
        HashSet<ulong> readGuard = null, setOne = null;
        Dictionary<ulong, string> result = null;

        for (int i = 0; i < instructions.Count; i++)
        {
            Instruction x = instructions[i];
            if (IsDirectMemoryOperand(x) && x.MemorySize == MemorySize.UInt8)
            {
                if (x.Mnemonic == Mnemonic.Mov && x.Op1Kind == OpKind.Immediate8 && x.Immediate8 == 1)
                    (setOne ??= new()).Add(x.MemoryDisplacement64);
                else if (x.Mnemonic == Mnemonic.Cmp || x.Mnemonic == Mnemonic.Test)
                    (readGuard ??= new()).Add(x.MemoryDisplacement64);
            }
            if (i > 0 && initMethod != 0 && x.Mnemonic == Mnemonic.Call
                && x.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64
                && x.NearBranchTarget == initMethod)
            {
                Instruction prev = instructions[i - 1];
                if (body.IsLinearStep(prev, x, acrossCalls: true) && prev.Mnemonic == Mnemonic.Push && IsDirectMemoryOperand(prev))
                {
                    (result ??= new())[prev.MemoryDisplacement64] = "inferred_method_init_token";
                }
            }
        }
        if (readGuard != null && setOne != null)
        {
            foreach (ulong addr in setOne)
            {
                if (readGuard.Contains(addr)) (result ??= new())[addr] = "inferred_method_init_flag";
            }
        }
        return result;
    }

    private static bool IsDirectMemoryOperand(in Instruction instruction)
        => instruction.Op0Kind == OpKind.Memory
        && instruction.MemoryIndex == Register.None
        && (instruction.MemoryBase == Register.None
            || instruction.MemoryBase == Register.RIP
            || instruction.MemoryBase == Register.EIP);

    private static void DetectIcallCacheIdiom(ApplicationAnalysisContext app, Il2CppNativeBody body,
        ref Dictionary<ulong, string> overrides)
    {
        List<Instruction> instructions = body.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            Instruction lea = instructions[i];
            if (lea.Mnemonic != Mnemonic.Lea || lea.Op1Kind != OpKind.Memory || lea.MemoryIndex != Register.None
                || (lea.MemoryBase != Register.None && lea.MemoryBase != Register.RIP && lea.MemoryBase != Register.EIP))
                continue;
            string signature = Il2CppAsmAnnotator.ReadCString(app, lea.MemoryDisplacement64);
            if (signature == null || !signature.Contains("::"))
                continue;
            ulong slot = 0;
            bool sawCall = false;
            for (int j = i + 1; j < instructions.Count && j <= i + 6; j++)
            {
                Instruction y = instructions[j];
                if (!body.IsLinearStep(instructions[j - 1], y, acrossCalls: true))
                    break;
                if (y.Mnemonic == Mnemonic.Call) { sawCall = true; continue; }
                if (sawCall && y.Mnemonic == Mnemonic.Mov && IsDirectMemoryOperand(y)
                    && y.Op1Kind == OpKind.Register && y.Op1Register == Register.RAX)
                {
                    slot = y.MemoryDisplacement64;
                    break;
                }
            }
            if (slot == 0)
                continue;

            bool readBefore = false;
            for (int k = i - 1; k >= 0 && k >= i - 10; k--)
            {
                Instruction z = instructions[k];
                if (z.Mnemonic == Mnemonic.Mov && z.Op0Kind == OpKind.Register && z.Op0Register == Register.RAX
                    && z.Op1Kind == OpKind.Memory && z.MemoryIndex == Register.None
                    && (z.MemoryBase == Register.None || z.MemoryBase == Register.RIP || z.MemoryBase == Register.EIP)
                    && z.MemoryDisplacement64 == slot)
                {
                    readBefore = true;
                    break;
                }
            }
            if (!readBefore)
                continue;

            (overrides ??= new())[slot] = "inferred_icall_cache<" + signature + ">";
        }
    }

    private static Dictionary<ulong, Il2CppAsmAnnotator.DataConstantOperand> CollectDataConstants(List<Instruction> instructions)
    {
        Dictionary<ulong, Il2CppAsmAnnotator.DataConstantOperand> result = null;
        foreach (Instruction instruction in instructions)
        {
            if (TryGetConstantOperand(instruction, out ulong virtualAddress, out Il2CppAsmAnnotator.DataConstantOperand operand))
            {
                (result ??= new Dictionary<ulong, Il2CppAsmAnnotator.DataConstantOperand>())[virtualAddress] = operand;
            }
        }
        return result;
    }

    private static bool TryGetConstantOperand(in Instruction instruction, out ulong virtualAddress, out Il2CppAsmAnnotator.DataConstantOperand operand)
    {
        virtualAddress = 0;
        operand = default;

        MemorySize memorySize = instruction.MemorySize;
        if (memorySize == MemorySize.Unknown) return false;

        if (instruction.MemoryIndex != Register.None) return false;
        Register memoryBase = instruction.MemoryBase;
        if (memoryBase != Register.None && memoryBase != Register.RIP && memoryBase != Register.EIP) return false;

        ulong address = instruction.MemoryDisplacement64;
        if (address < 0x10000) return false;
        MemorySizeInfo info = memorySize.GetInfo();
        if (info.ElementSize <= 0 || info.ElementCount <= 0) return false;

        bool isFloat = IsFloatElement(info.ElementType) && !IsBitwiseFloatLogical(instruction.Mnemonic);
        if (isFloat)
        {
            if (info.ElementSize != 2 && info.ElementSize != 4 && info.ElementSize != 8) return false;
        }
        else
        {
            if (info.ElementSize != 1 && info.ElementSize != 2 && info.ElementSize != 4 && info.ElementSize != 8) return false;
        }

        virtualAddress = address;
        operand = new Il2CppAsmAnnotator.DataConstantOperand(info.ElementSize, info.ElementCount, isFloat);
        return true;
    }

    private static bool IsFloatElement(MemorySize elementType)
        => elementType == MemorySize.Float16
        || elementType == MemorySize.Float32
        || elementType == MemorySize.Float64;

    private static bool IsBitwiseFloatLogical(Mnemonic mnemonic)
        => mnemonic is Mnemonic.Andps or Mnemonic.Andnps or Mnemonic.Orps or Mnemonic.Xorps
            or Mnemonic.Andpd or Mnemonic.Andnpd or Mnemonic.Orpd or Mnemonic.Xorpd
            or Mnemonic.Vandps or Mnemonic.Vandnps or Mnemonic.Vorps or Mnemonic.Vxorps
            or Mnemonic.Vandpd or Mnemonic.Vandnpd or Mnemonic.Vorpd or Mnemonic.Vxorpd;

    public static Dictionary<ulong, string> TraceRuntimeGlobals(ApplicationAnalysisContext app)
    {
        Dictionary<ulong, string> map = new();
        var binary = app.Binary;
        ulong initVa = binary.GetVirtualAddressOfExportedFunctionByName("il2cpp_init");
        if (initVa == 0) return map;

        HashSet<ulong> visited = new();
        Queue<ulong> queue = new();
        queue.Enqueue(initVa);
        List<List<Instruction>> functions = new();
        int budget = 0;
        while (queue.Count > 0 && budget < 20000)
        {
            ulong funcVa = queue.Dequeue();
            if (!visited.Add(funcVa)) continue;
            budget++;
            List<Instruction> insns = Il2CppNativeBody.Read(app, funcVa).Instructions;
            foreach (Instruction ins in insns)
            {
                if ((ins.Mnemonic == Mnemonic.Call || ins.Mnemonic == Mnemonic.Jmp)
                    && ins.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32)
                {
                    ulong target = ins.NearBranchTarget;
                    if (!visited.Contains(target) && visited.Count + queue.Count < 20000) queue.Enqueue(target);
                }
            }
            functions.Add(insns);
        }

        ulong headerSlot = 0;
        foreach (List<Instruction> insns in functions)
        {
            if (TryFindMetadataPair(insns, out ulong slotBase, out ulong slotHeader))
            {
                map[slotBase] = "s_GlobalMetadata";
                map[slotHeader] = "s_GlobalMetadataHeader";
                headerSlot = slotHeader;
                break;
            }
        }
        if (headerSlot != 0)
        {
            foreach (List<Instruction> insns in functions)
            {
                if (TryFindStringLiteralCache(insns, headerSlot, out ulong slotCache))
                {
                    map[slotCache] = "s_StringLiteralTable";
                    break;
                }
            }
        }
        Logger.Info(LogCategory.Export,
            $"[Il2CppMethodDump] traced {map.Count} il2cpp runtime global(s) from il2cpp_init: {string.Join(", ", map.Values)}");
        return map;
    }

    private static bool IsDirectStoreOfRegister(in Instruction x, Register reg)
        => x.Mnemonic == Mnemonic.Mov && x.Op0Kind == OpKind.Memory
        && x.MemoryIndex == Register.None
        && (x.MemoryBase == Register.None || x.MemoryBase == Register.RIP || x.MemoryBase == Register.EIP)
        && x.Op1Kind == OpKind.Register && x.Op1Register == reg;

    private static bool TryFindMetadataPair(List<Instruction> insns, out ulong slotBase, out ulong slotHeader)
    {
        slotBase = 0;
        slotHeader = 0;
        for (int i = 1; i < insns.Count; i++)
        {
            if (insns[i - 1].Mnemonic != Mnemonic.Call || !IsDirectStoreOfRegister(insns[i], Register.RAX)) continue;
            ulong a = insns[i].MemoryDisplacement64;
            bool sawDeref = false;
            for (int j = i + 1; j < insns.Count && j < i + 18; j++)
            {
                Instruction y = insns[j];
                if (y.Mnemonic == Mnemonic.Call) break;
                if (y.Op0Kind == OpKind.Register && y.Op0Register == Register.RAX
                    && y.Mnemonic != Mnemonic.Cmp && y.Mnemonic != Mnemonic.Test) break;
                if (y.MemoryBase == Register.RAX && y.MemoryIndex == Register.None) sawDeref = true;
                if (sawDeref && IsDirectStoreOfRegister(y, Register.RAX))
                {
                    ulong b = y.MemoryDisplacement64;
                    if (b != a) { slotBase = a; slotHeader = b; return true; }
                }
            }
        }
        return false;
    }

    private static bool TryFindStringLiteralCache(List<Instruction> insns, ulong headerSlot, out ulong slotCache)
    {
        slotCache = 0;
        for (int i = 0; i < insns.Count; i++)
        {
            Instruction x = insns[i];
            if (x.Mnemonic != Mnemonic.Mov || x.Op0Kind != OpKind.Register || x.Op0Register != Register.RAX) continue;
            if (x.Op1Kind != OpKind.Memory || x.MemoryIndex != Register.None) continue;
            if (x.MemoryBase != Register.RIP && x.MemoryBase != Register.None) continue;
            if (x.MemoryDisplacement64 != headerSlot) continue;

            for (int j = i + 1; j < insns.Count && j < i + 8; j++)
            {
                Instruction y = insns[j];
                if (y.Op1Kind != OpKind.Memory || y.MemoryBase != Register.RAX || y.MemoryIndex != Register.None || y.MemoryDisplacement64 != 0x0C) continue;
                for (int k = j + 1; k < insns.Count && k < j + 14; k++)
                {
                    if (insns[k].Mnemonic != Mnemonic.Call) continue;
                    for (int m = k + 1; m < insns.Count && m < k + 4; m++)
                    {
                        if (IsDirectStoreOfRegister(insns[m], Register.RAX)) { slotCache = insns[m].MemoryDisplacement64; return true; }
                    }
                    break;
                }
                break;
            }
        }
        return false;
    }
}
