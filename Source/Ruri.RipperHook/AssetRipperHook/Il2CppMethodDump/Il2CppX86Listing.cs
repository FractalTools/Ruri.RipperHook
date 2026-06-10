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
            sb.Append(Il2CppAsmAnnotator.AnnotateLine(app, output.ToStringAndReset(), overrides)).Append('\n');
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
            if (IsAbsoluteMemory(x) && x.MemorySize == MemorySize.UInt8 && x.Op1Kind == OpKind.Immediate8)
            {
                if (x.Mnemonic == Mnemonic.Cmp && x.Immediate8 == 0) (cmpZero ??= new()).Add(x.MemoryDisplacement64);
                else if (x.Mnemonic == Mnemonic.Mov && x.Immediate8 == 1) (movOne ??= new()).Add(x.MemoryDisplacement64);
            }
            if (i > 0 && initMethod != 0 && x.Mnemonic == Mnemonic.Call
                && x.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64
                && x.NearBranchTarget == initMethod)
            {
                Instruction prev = instructions[i - 1];
                if (prev.Mnemonic == Mnemonic.Push && IsAbsoluteMemory(prev))
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

    // ds:[abs]：第一操作数是无基址/无变址的绝对内存寻址。
    private static bool IsAbsoluteMemory(in Instruction instruction)
        => instruction.Op0Kind == OpKind.Memory
        && instruction.MemoryBase == Register.None
        && instruction.MemoryIndex == Register.None;
}
