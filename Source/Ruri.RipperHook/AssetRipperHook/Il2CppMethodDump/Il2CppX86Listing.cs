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
    /// </summary>
    private static Dictionary<ulong, string> DetectMetadataInitIdiom(ApplicationAnalysisContext app, List<Instruction> instructions)
    {
        ulong initMethod = Il2CppAsmAnnotator.KeyFunctionAddress(app, "initialize_method");
        HashSet<ulong> cmpZero = null, movOne = null;
        Dictionary<ulong, string> result = null;

        for (int i = 0; i < instructions.Count; i++)
        {
            Instruction x = instructions[i];
            if (IsDirectMemoryOperand(x) && x.MemorySize == MemorySize.UInt8 && x.Op1Kind == OpKind.Immediate8)
            {
                if (x.Mnemonic == Mnemonic.Cmp && x.Immediate8 == 0) (cmpZero ??= new()).Add(x.MemoryDisplacement64);
                else if (x.Mnemonic == Mnemonic.Mov && x.Immediate8 == 1) (movOne ??= new()).Add(x.MemoryDisplacement64);
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
        if (cmpZero != null && movOne != null)
        {
            foreach (ulong addr in cmpZero)
            {
                if (movOne.Contains(addr)) (result ??= new())[addr] = "method_init_flag";
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
        if (info.ElementCount == 1 && !isFloat) return false;                                  // 标量整数：歧义（可能是运行期全局指针）→ 保留 g_
        if (isFloat && info.ElementSize != 2 && info.ElementSize != 4 && info.ElementSize != 8) return false; // 非常规浮点宽度（Float80/128/bf16）不解

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
}
