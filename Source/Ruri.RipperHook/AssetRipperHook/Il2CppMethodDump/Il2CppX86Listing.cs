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
            sb.Append(Il2CppAsmAnnotator.AnnotateLine(app, output.ToStringAndReset())).Append('\n');
        }
        return sb.ToString();
    }
}
