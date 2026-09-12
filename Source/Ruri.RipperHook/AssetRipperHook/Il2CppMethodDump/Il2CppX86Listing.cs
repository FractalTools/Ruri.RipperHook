extern alias icedreal;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using icedreal::Iced.Intel;
namespace Ruri.RipperHook.AR;

internal static class Il2CppX86Listing
{
    private static ApplicationAnalysisContext _condemnationApp;
    private static readonly HashSet<AssemblyAnalysisContext> _condemnationScanned = new();

    private static List<Instruction> DecodeInstructions(byte[] bytes, ulong start, bool is32)
    {
        ulong end = start + (ulong)bytes.Length;
        Decoder decoder = Decoder.Create(is32 ? 32 : 64, new ByteArrayCodeReader(bytes), start);
        List<Instruction> instructions = new();
        while (decoder.IP < end)
        {
            decoder.Decode(out Instruction instruction);
            if (instruction.IsInvalid) break;
            instructions.Add(instruction);
        }
        return instructions;
    }

    private static void EnsureCondemnationScan(ApplicationAnalysisContext app, Il2CppTypeModel model, MethodAnalysisContext current)
    {
        if (!ReferenceEquals(_condemnationApp, app)) { _condemnationApp = app; _condemnationScanned.Clear(); }
        AssemblyAnalysisContext assembly = current.DeclaringType?.DeclaringAssembly;
        if (assembly == null || !_condemnationScanned.Add(assembly)) return;        try
        {
            bool is32 = LibCpp2IlMain.Binary.is32Bit;
            {
                foreach (TypeAnalysisContext type in assembly.Types)
                {
                    foreach (MethodAnalysisContext method in type.Methods)
                    {
                        if (method.UnderlyingPointer == 0) continue;
                        try
                        {
                            method.EnsureRawBytes();
                            byte[] bytes = method.RawBytes.ToArray();
                            if (bytes.Length == 0) continue;
                            List<Instruction> insns = DecodeInstructions(bytes, method.UnderlyingPointer, is32);
                            if (insns.Count == 0) continue;
                            new Il2CppRegisterFlow(app, method, insns, model).Analyze();                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    public static string Render(ApplicationAnalysisContext app, MethodAnalysisContext method)
    {
        method.EnsureRawBytes();
        byte[] bytes = method.RawBytes.ToArray();
        if (bytes.Length == 0) return string.Empty;

        ulong start = method.UnderlyingPointer;
        ulong end = start + (ulong)bytes.Length;
        bool is32 = LibCpp2IlMain.Binary.is32Bit;

        List<Instruction> instructions = DecodeInstructions(bytes, start, is32);

        HashSet<ulong> labels = new();
        foreach (Instruction instruction in instructions)
        {
            if (instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
            {
                ulong target = instruction.NearBranchTarget;
                if (target >= start && target < end) labels.Add(target);
            }
        }

        Dictionary<ulong, string> overrides = DetectMetadataInitIdiom(app, instructions);
        DetectIcallCacheIdiom(instructions, ref overrides);

        Dictionary<ulong, Il2CppAsmAnnotator.DataConstantOperand> dataConstants = CollectDataConstants(instructions);

        Il2CppTypeModel model = Il2CppTypeModel.Get(app);
        EnsureCondemnationScan(app, model, method);
        Il2CppRegisterFlow flow = new(app, method, instructions, model);
        flow.Analyze();

        Il2CppSymbolResolver resolver = new(app, overrides, dataConstants);
        MasmFormatter formatter = new(resolver);
        StringOutput output = new();
        System.Text.StringBuilder sb = new(bytes.Length * 6);
        for (int i = 0; i < instructions.Count; i++)
        {
            Instruction instruction = instructions[i];
            if (labels.Contains(instruction.IP))
            {
                sb.Append("loc_").Append(instruction.IP.ToString("X")).Append(":\n");
            }
            formatter.Format(instruction, output);
            sb.Append(output.ToStringAndReset());
            string comment = flow.CommentAt(i);
            if (comment != null)
            {
                sb.Append("  ; ").Append(comment);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static Dictionary<ulong, string> DetectMetadataInitIdiom(ApplicationAnalysisContext app, List<Instruction> instructions)
    {
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
                if (prev.Mnemonic == Mnemonic.Push && IsDirectMemoryOperand(prev))
                {
                    (result ??= new())[prev.MemoryDisplacement64] = "method_init_token";
                }
            }
        }
        if (readGuard != null && setOne != null)
        {
            foreach (ulong addr in setOne)
            {
                if (readGuard.Contains(addr)) (result ??= new())[addr] = "method_init_flag";
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

    private static void DetectIcallCacheIdiom(List<Instruction> instructions, ref Dictionary<ulong, string> overrides)
    {
        for (int i = 0; i < instructions.Count; i++)
        {
            Instruction lea = instructions[i];
            if (lea.Mnemonic != Mnemonic.Lea || lea.Op1Kind != OpKind.Memory || lea.MemoryIndex != Register.None
                || (lea.MemoryBase != Register.None && lea.MemoryBase != Register.RIP && lea.MemoryBase != Register.EIP))
                continue;
            string signature = Il2CppAsmAnnotator.ReadCString(lea.MemoryDisplacement64);
            if (signature == null || !signature.Contains("::"))
                continue;
            ulong slot = 0;
            bool sawCall = false;
            for (int j = i + 1; j < instructions.Count && j <= i + 6; j++)
            {
                Instruction y = instructions[j];
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

            (overrides ??= new())[slot] = "icall<" + signature + ">";
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
            if (info.ElementSize != 2 && info.ElementSize != 4 && info.ElementSize != 8) return false;        }
        else
        {
            if (info.ElementSize != 1 && info.ElementSize != 2 && info.ElementSize != 4 && info.ElementSize != 8) return false;        }

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
        try
        {
            var binary = LibCpp2IlMain.Binary;
            if (binary == null) return map;
            ulong initVa = binary.GetVirtualAddressOfExportedFunctionByName("il2cpp_init");
            if (initVa == 0) return map;
            int bitness = binary.is32Bit ? 32 : 64;

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
                long off = binary.MapVirtualAddressToRaw(funcVa, false);
                if (off < 0) continue;
                byte[] code;
                try { code = binary.ReadByteArrayAtRawAddress(off, 0x4000); }
                catch { continue; }
                if (code == null || code.Length == 0) continue;

                ByteArrayCodeReader reader = new(code);
                Decoder decoder = Decoder.Create(bitness, reader, funcVa);
                List<Instruction> insns = new(128);
                int guard = 0;
                while (guard++ < 4000)
                {
                    decoder.Decode(out Instruction ins);
                    if (ins.IsInvalid) break;
                    insns.Add(ins);
                    if ((ins.Mnemonic == Mnemonic.Call || ins.Mnemonic == Mnemonic.Jmp)
                        && ins.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32)
                    {
                        ulong target = ins.NearBranchTarget;
                        if (!visited.Contains(target) && visited.Count + queue.Count < 20000) queue.Enqueue(target);
                    }
                    if (ins.Mnemonic == Mnemonic.Ret || ins.Mnemonic == Mnemonic.Int3) break;
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
            System.Console.WriteLine($"    [+] AR_Il2CppMethodDump: traced {map.Count} il2cpp runtime global(s) from il2cpp_init: {string.Join(", ", map.Values)}");
        }
        catch { }
        return map;
    }

    private static bool IsDirectStoreOfRegister(in Instruction x, Register reg)
        => x.Mnemonic == Mnemonic.Mov && x.Op0Kind == OpKind.Memory
        && x.MemoryIndex == Register.None
        && (x.MemoryBase == Register.None || x.MemoryBase == Register.RIP || x.MemoryBase == Register.EIP)
        && x.Op1Kind == OpKind.Register && x.Op1Register == reg;

    private static bool TryFindMetadataPair(List<Instruction> insns, out ulong slotBase, out ulong slotHeader)
    {
        slotBase = 0; slotHeader = 0;
        for (int i = 1; i < insns.Count; i++)
        {
            if (insns[i - 1].Mnemonic != Mnemonic.Call || !IsDirectStoreOfRegister(insns[i], Register.RAX)) continue;
            ulong a = insns[i].MemoryDisplacement64;
            bool sawDeref = false;
            for (int j = i + 1; j < insns.Count && j < i + 18; j++)
            {
                Instruction y = insns[j];
                if (y.Mnemonic == Mnemonic.Call) break;                if (y.Op0Kind == OpKind.Register && y.Op0Register == Register.RAX
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
