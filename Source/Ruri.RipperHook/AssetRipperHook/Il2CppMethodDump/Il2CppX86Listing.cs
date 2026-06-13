extern alias icedreal;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using icedreal::Iced.Intel; // 真 Iced（MonoMod.Iced 也有 Iced.Intel，靠 alias 区分）

namespace Ruri.RipperHook.AR;

/// <summary>
/// 用 Iced 自己解码方法的原生字节（而非 Cpp2IL 的扁平 PrintAssembly），从而拿到**每条指令的地址**，
/// 据此把函数内的短跳/近跳目标渲染成独立的 <c>loc_XXXX:</c> 标签行——形成可读的汇编块（看得出每个跳转落在哪）。
/// 每条指令文本再交给 <see cref="Il2CppAsmAnnotator.AnnotateLine"/> 把操作数地址替换成符号。
/// 仅用于 x86（32/64）；ARM 等走 PrintAssembly 回退（无标签）。
/// </summary>
internal static class Il2CppX86Listing
{
    public static string Render(ApplicationAnalysisContext app, MethodAnalysisContext method)
    {
        method.EnsureRawBytes();
        byte[] bytes = method.RawBytes.ToArray();
        if (bytes.Length == 0) return string.Empty;

        ulong start = method.UnderlyingPointer;
        ulong end = start + (ulong)bytes.Length;
        bool is32 = LibCpp2IlMain.Binary.is32Bit;

        ByteArrayCodeReader reader = new(bytes);
        Decoder decoder = Decoder.Create(is32 ? 32 : 64, reader, start);
        List<Instruction> instructions = new();
        while (decoder.IP < end)
        {
            decoder.Decode(out Instruction instruction);
            if (instruction.IsInvalid) break;
            instructions.Add(instruction);
        }

        // 收集落在本方法内的近跳目标 → 需要标签的地址。
        HashSet<ulong> labels = new();
        foreach (Instruction instruction in instructions)
        {
            if (instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
            {
                ulong target = instruction.NearBranchTarget;
                if (target >= start && target < end) labels.Add(target);
            }
        }

        // 识别 il2cpp 元数据初始化惯用法，给那两个无名全局起语义名。
        Dictionary<ulong, string> overrides = DetectMetadataInitIdiom(app, instructions);

        // 预判常量池操作数（直接寻址、浮点 / 向量）：注解层据此把它们的文件字节解引用成实际值，替代裸 g_ 指针。
        Dictionary<ulong, Il2CppAsmAnnotator.DataConstantOperand> dataConstants = CollectDataConstants(instructions);

        // 每个 Render 用全新的 formatter/output（Cpp2IL 的是 static、非线程安全；这里本地实例即可）。
        MasmFormatter formatter = new();
        StringOutput output = new();
        System.Text.StringBuilder sb = new(bytes.Length * 6);
        foreach (Instruction instruction in instructions)
        {
            if (labels.Contains(instruction.IP))
            {
                sb.Append("loc_").Append(instruction.IP.ToString("X")).Append(":\n");
            }
            formatter.Format(instruction, output);
            sb.Append(Il2CppAsmAnnotator.AnnotateLine(app, output.ToStringAndReset(), overrides, dataConstants)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 识别 il2cpp 每方法元数据初始化惯用法：
    /// <c>cmp byte ptr [X],0 / jne / push [Y] / call il2cpp_codegen_initialize_method / mov byte ptr [X],1</c>。
    /// X = 本方法 "元数据已初始化" 标志，Y = 传给初始化器的元数据 token。两者元数据里都没有名字，这里起语义名。
    /// 守卫读取既可能是 <c>cmp byte ptr [X],0</c>，也可能是编译器先清零寄存器再 <c>cmp byte ptr [X],sil</c>，
    /// 或 <c>test byte ptr [X],…</c>——只要该字节既被读作守卫又被 <c>mov byte ptr [X],1</c> 置位，就是一次性标志。
    /// </summary>
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
                // 置位：mov byte ptr [X],1（初始化完成标记）。
                if (x.Mnemonic == Mnemonic.Mov && x.Op1Kind == OpKind.Immediate8 && x.Immediate8 == 1)
                    (setOne ??= new()).Add(x.MemoryDisplacement64);
                // 守卫读取：cmp / test 该字节（对端是立即数 0 还是清零寄存器都算）。
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

    // 第一操作数是"直接寻址"的内存：无变址，基址为空（32 位绝对 [disp]）或 RIP/EIP（64 位 IP 相对——
    // 其 MemoryDisplacement64 即解析后的绝对目标，与格式化器打印的绝对地址、注解键一致）。
    private static bool IsDirectMemoryOperand(in Instruction instruction)
        => instruction.Op0Kind == OpKind.Memory
        && instruction.MemoryIndex == Register.None
        && (instruction.MemoryBase == Register.None
            || instruction.MemoryBase == Register.RIP
            || instruction.MemoryBase == Register.EIP);

    /// <summary>
    /// 预判本方法里所有"直接寻址的常量池操作数"：浮点标量、以及任意向量（含整型 SIMD 掩码）。
    /// 返回 绝对地址 → 形状；标量整数刻意排除（其文件字节常是运行期才填充的全局指针，并非常量）。
    /// 注解层在所有元数据都未命中后据此把文件字节解引用成实际值。
    /// </summary>
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

        // 必须真正解引用内存且元素大小已知（自动排除 lea / 纯寄存器指令——它们 MemorySize 为 Unknown）。
        MemorySize memorySize = instruction.MemorySize;
        if (memorySize == MemorySize.Unknown) return false;

        // 仅直接目标：无变址，基址为空或 RIP/EIP。
        if (instruction.MemoryIndex != Register.None) return false;
        Register memoryBase = instruction.MemoryBase;
        if (memoryBase != Register.None && memoryBase != Register.RIP && memoryBase != Register.EIP) return false;

        ulong address = instruction.MemoryDisplacement64;
        if (address < 0x10000) return false; // 小立即数 / 栈相关，绝不会是全局常量

        MemorySizeInfo info = memorySize.GetInfo();
        if (info.ElementSize <= 0 || info.ElementCount <= 0) return false;

        // andps/andnps/orps/xorps（+pd、+V 变体）是位运算：其浮点类型操作数实为位掩码（abs/sign 掩码等），
        // 字节值当浮点读会出 NaN/-0 等误导文本 → 强制按十六进制渲染，而非浮点。
        bool isFloat = IsFloatElement(info.ElementType) && !IsBitwiseFloatLogical(instruction.Mnemonic);
        // 元素宽度可还原校验：浮点 2/4/8；整数（含位掩码 / 标量整数）1/2/4/8。标量整数是否真常量由注解层
        // 按"只读且已落盘的 PE 段"门控（Il2CppAsmAnnotator.ConstantAddressAllowed），避免把 .data 运行期
        // 全局指针当常量——故此处放行标量整数、交给注解层裁决。
        if (isFloat)
        {
            if (info.ElementSize != 2 && info.ElementSize != 4 && info.ElementSize != 8) return false; // 非常规浮点宽度（Float80/128/bf16）不解
        }
        else
        {
            if (info.ElementSize != 1 && info.ElementSize != 2 && info.ElementSize != 4 && info.ElementSize != 8) return false; // 非 2 幂整数元素宽度不解
        }

        virtualAddress = address;
        operand = new Il2CppAsmAnnotator.DataConstantOperand(info.ElementSize, info.ElementCount, isFloat);
        return true;
    }

    private static bool IsFloatElement(MemorySize elementType)
        => elementType == MemorySize.Float16
        || elementType == MemorySize.Float32
        || elementType == MemorySize.Float64;

    // 浮点位运算（内存操作数是位掩码、不是浮点值）：andps/andnps/orps/xorps + pd + V 变体。
    private static bool IsBitwiseFloatLogical(Mnemonic mnemonic)
        => mnemonic is Mnemonic.Andps or Mnemonic.Andnps or Mnemonic.Orps or Mnemonic.Xorps
            or Mnemonic.Andpd or Mnemonic.Andnpd or Mnemonic.Orpd or Mnemonic.Xorpd
            or Mnemonic.Vandps or Mnemonic.Vandnps or Mnemonic.Vorps or Mnemonic.Vxorps
            or Mnemonic.Vandpd or Mnemonic.Vandnpd or Mnemonic.Vorpd or Mnemonic.Vxorpd;

    /// <summary>
    /// 静态追踪 <c>il2cpp_init</c> 的调用图，给少数被高频引用的 il2cpp 运行时全局槽（.bss——运行期才填充、
    /// 文件里无值，故无法静态读出"值"，但其"身份"可由 init 代码权威确定）命名。纯反汇编 + 与 global-metadata
    /// 头部布局核对，绝不执行任何代码：
    /// <list type="bullet">
    /// <item><c>s_GlobalMetadata</c> / <c>s_GlobalMetadataHeader</c>：<c>GlobalMetadata::Initialize</c> 里同一
    /// MapMetadata 结果先后存入相邻两槽，其一随即被按头部字段（<c>[rax+imm]</c>）解引用；先存者=base，后存者=header。</item>
    /// <item><c>s_StringLiteralTable</c>：按 <c>header.stringLiteralSize</c>（<c>[header+0xC]</c>，标准 v29 头）
    /// 分配、按字面量索引惰性填充的 <c>Il2CppString*</c> 缓存。</item>
    /// </list>
    /// 找不到（非 PE / 布局不符）就返回空表，优雅降级。
    /// </summary>
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

    // mov [直接寻址], <reg>（.bss/.data 槽 = 寄存器）。
    private static bool IsDirectStoreOfRegister(in Instruction x, Register reg)
        => x.Mnemonic == Mnemonic.Mov && x.Op0Kind == OpKind.Memory
        && x.MemoryIndex == Register.None
        && (x.MemoryBase == Register.None || x.MemoryBase == Register.RIP || x.MemoryBase == Register.EIP)
        && x.Op1Kind == OpKind.Register && x.Op1Register == reg;

    // call; mov [A],rax; …(rax 被 [rax+imm] 解引用、未被重写)…; mov [B],rax  → A=base, B=header。
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
                if (y.Mnemonic == Mnemonic.Call) break;                                  // rax 被调用结果覆盖
                // rax 被重写才终止；cmp/test 只读 Op0、不写，不能算重写（否则 `test rax,rax` 会误截断）。
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

    // mov rax,[header]; …(reg)←[rax+0Ch]…; call; mov [C],rax  → C = 字符串字面量缓存。
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
