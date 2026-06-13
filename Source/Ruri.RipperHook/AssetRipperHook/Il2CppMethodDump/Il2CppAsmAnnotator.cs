using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using LibCpp2IL;
using Cpp2IL.Core.Model.Contexts;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 把 PrintAssembly 里的裸地址**就地替换**成符号（全部来自 GlobalMetadata + 二进制符号信息），不再保留指针：
/// <list type="bullet">
/// <item>调用/数据精确命中托管方法 → 方法全名（<c>call Cloth__base::checkRequirements</c>）；</item>
/// <item>il2cpp 运行时关键函数 → 函数名（<c>call il2cpp_codegen_initialize_method</c>）；</item>
/// <item>数据全局 → 字符串字面量 / TypeInfo / 方法 / 字段（<c>ds:["…"]</c>、<c>ds:[UnityEngine.Debug_TypeInfo]</c>）；</item>
/// <item>方法体内的分支目标 → <c>loc_XXXX</c>；区域外、无名的运行时函数 → <c>sub_XXXX</c>；</item>
/// <item>常量池数据（浮点 / 向量常量）→ 直接经文件偏移解引用出实际值（<c>movss xmm0,[360f]</c>、<c>[{7FFFFFFFh x4}]</c>），不再给裸指针；</item>
/// <item>仍解析不到的数据 → <c>g_XXXX</c>（无名 codegen 全局；标量整数全局亦归此——其文件字节可能运行期才填充，并非常量）。</item>
/// </list>
/// 调用方 <see cref="Il2CppAsmLookup.GetDisassembly"/> 持锁，故静态缓存的读写是串行安全的。
/// </summary>
internal static class Il2CppAsmAnnotator
{
    /// <summary>
    /// X86 列表层对一处"直接寻址的内存操作数"预判出的常量池形状：元素字节宽 + 元素个数 + 是否浮点。
    /// <see cref="Resolve"/> 在所有元数据都未命中后，据此把该地址的文件字节解引用成实际值（而非裸 g_ 指针）。
    /// </summary>
    public readonly struct DataConstantOperand
    {
        public readonly int ElementSize;
        public readonly int ElementCount;
        public readonly bool IsFloatElement;

        public DataConstantOperand(int elementSize, int elementCount, bool isFloatElement)
        {
            ElementSize = elementSize;
            ElementCount = elementCount;
            IsFloatElement = isFloatElement;
        }
    }

    // MASM 形如 10278DB0h；也兼容 0x10278DB0（ARM 等其它格式化器）。
    private static readonly Regex HexToken =
        new(@"0x(?<a>[0-9A-Fa-f]+)|\b(?<b>[0-9A-Fa-f]+)h\b", RegexOptions.Compiled);

    private static ApplicationAnalysisContext _app;
    private static Dictionary<ulong, string> _keyFunctions;
    private static Dictionary<ulong, string> _exports; // PE 导出表 VA→名（权威）
    private static ulong[] _sortedMethodStarts;
    private static readonly Dictionary<ulong, string> _globalCache = new();

    /// <summary>整段反汇编：逐行替换地址为符号。给 ARM 等走 PrintAssembly 的回退路径用。</summary>
    public static string Annotate(ApplicationAnalysisContext app, string asmText)
    {
        EnsureMaps(app);
        StringBuilder sb = new(asmText.Length + 32);
        foreach (string rawLine in asmText.Split('\n'))
        {
            sb.Append(AnnotateLine(app, rawLine.TrimEnd('\r'))).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 单行替换。X86 列表渲染器逐指令调用，可传入 <paramref name="overrides"/>（本方法专属的地址→符号，
    /// 如 il2cpp 元数据初始化惯用法识别出的 method_init_flag / method_init_token）。
    /// </summary>
    public static string AnnotateLine(ApplicationAnalysisContext app, string line, IReadOnlyDictionary<ulong, string> overrides = null, IReadOnlyDictionary<ulong, DataConstantOperand> dataConstants = null)
    {
        EnsureMaps(app);
        return HexToken.Replace(line, m => ReplaceToken(line, m, overrides, dataConstants));
    }

    /// <summary>关键函数（il2cpp 运行时函数）名→地址反查；找不到返回 0。</summary>
    public static ulong KeyFunctionAddress(ApplicationAnalysisContext app, string nameContains)
    {
        EnsureMaps(app);
        foreach (KeyValuePair<ulong, string> kv in _keyFunctions)
        {
            if (kv.Value.Contains(nameContains)) return kv.Key;
        }
        return 0;
    }

    private static string ReplaceToken(string line, Match m, IReadOnlyDictionary<ulong, string> overrides, IReadOnlyDictionary<ulong, DataConstantOperand> dataConstants)
    {
        string hex = m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value;
        if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong addr)) return m.Value;
        if (addr < 0x10000) return m.Value; // 小立即数 / 寄存器相对偏移 / 8 位寄存器名 (ah/bh…)
        return Resolve(addr, IsInBrackets(line, m.Index), overrides, dataConstants) ?? m.Value;
    }

    private static bool IsInBrackets(string line, int idx)
    {
        int depth = 0;
        for (int i = 0; i < idx && i < line.Length; i++)
        {
            if (line[i] == '[') depth++;
            else if (line[i] == ']') depth--;
        }
        return depth > 0;
    }

    private static string Resolve(ulong addr, bool inBrackets, IReadOnlyDictionary<ulong, string> overrides, IReadOnlyDictionary<ulong, DataConstantOperand> dataConstants)
    {
        // 本方法专属覆盖（惯用法识别出的 init flag / token）优先。
        if (overrides != null && overrides.TryGetValue(addr, out string ov))
            return ov;
        if (_app.MethodsByAddress.TryGetValue(addr, out List<MethodAnalysisContext> methods) && methods.Count > 0)
            return methods[0].FullName;
        if (_exports.TryGetValue(addr, out string export)) // PE 导出表：权威符号
            return export;
        if (_keyFunctions.TryGetValue(addr, out string keyFunc))
            return keyFunc;
        if (!_globalCache.TryGetValue(addr, out string global))
        {
            global = ResolveGlobal(addr);
            _globalCache[addr] = global;
        }
        if (global != null)
            return global;

        // 未命中任何元数据。数据引用：先把"常量池"地址解引用成实际值（浮点 / 向量常量——字节即真值、
        // 经文件偏移可还原；由 X86 列表层按操作数大小/元素类型预判）。标量整数不在此列（其文件字节可能是
        // 运行期才填充的全局指针，并非常量）。还原不出再退回 g_。
        if (inBrackets)
        {
            if (dataConstants != null && dataConstants.TryGetValue(addr, out DataConstantOperand operand))
            {
                string constant = TryReadDataConstant(addr, operand);
                if (constant != null)
                    return constant;
            }
            return "g_" + addr.ToString("X"); // 无名 codegen 全局，元数据里没有它的名字
        }
        // 代码目标：方法体内分支 → loc_，区域外无名运行时函数 → sub_。
        return (InMethodBody(addr) ? "loc_" : "sub_") + addr.ToString("X");
    }

    private static bool InMethodBody(ulong addr)
    {
        ulong[] starts = _sortedMethodStarts;
        int lo = 0, hi = starts.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (starts[mid] <= addr) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        // 严格落在相邻两个方法起点之间 ⇒ 在某方法体内部（起点本身已被 MethodsByAddress 命中）。
        return best >= 0 && best + 1 < starts.Length && addr > starts[best] && addr < starts[best + 1];
    }

    private static string ResolveGlobal(ulong addr)
    {
        try
        {
            string literal = LibCpp2IlMain.GetLiteralByAddress(addr);
            if (literal != null) return "\"" + Escape(literal) + "\"";
        }
        catch { }
        try
        {
            MetadataUsage usage = LibCpp2IlMain.GetAnyGlobalByAddress(addr);
            if (usage?.Value != null)
            {
                string value = usage.Value.ToString();
                return usage.Type.ToString().Contains("Type") ? value + "_TypeInfo" : value;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 把 <paramref name="virtualAddress"/> 处的常量池字节解引用成实际值文本（浮点标量 / 向量常量）。
    /// 经 <see cref="LibCpp2IlMain.Binary"/> 把 VA 映射成文件原始偏移，再读出 <paramref name="operand"/>
    /// 指定的字节并按元素类型还原。映射不到文件实体字节（如 .bss / 未初始化区）时返回 null —— 调用方据此退回 g_。
    /// </summary>
    private static string TryReadDataConstant(ulong virtualAddress, in DataConstantOperand operand)
    {
        Il2CppBinary binary = LibCpp2IlMain.Binary;
        if (binary == null)
            return null;

        int total = operand.ElementSize * operand.ElementCount;
        if (total <= 0 || total > 64) // 至多 512-bit
            return null;

        long raw;
        try
        {
            if (!binary.TryMapVirtualAddressToRaw(virtualAddress, out raw))
                return null;
        }
        catch { return null; }
        if (raw < 0 || raw + total > binary.RawLength)
            return null;

        Span<byte> buffer = stackalloc byte[64];
        Span<byte> bytes = buffer.Slice(0, total);
        try
        {
            for (int i = 0; i < total; i++)
            {
                bytes[i] = binary.GetByteAtRawAddress((ulong)(raw + i));
            }
        }
        catch { return null; }

        return operand.ElementCount == 1
            ? FormatElement(bytes, operand.IsFloatElement, operand.ElementSize)
            : FormatPackedConstant(bytes, operand);
    }

    private static string FormatPackedConstant(ReadOnlySpan<byte> bytes, in DataConstantOperand operand)
    {
        int size = operand.ElementSize;
        int count = operand.ElementCount;

        // 向量常量（尤其 SIMD 掩码）各分量多半相同：等值则折成 {elem xN}。
        ReadOnlySpan<byte> first = bytes.Slice(0, size);
        bool allEqual = true;
        for (int i = 1; i < count; i++)
        {
            if (!bytes.Slice(i * size, size).SequenceEqual(first)) { allEqual = false; break; }
        }
        if (allEqual)
        {
            return "{" + FormatElement(first, operand.IsFloatElement, size) + " x" + count.ToString(CultureInfo.InvariantCulture) + "}";
        }

        StringBuilder sb = new(count * 10 + 2);
        sb.Append('{');
        int shown = count < 8 ? count : 8;
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(FormatElement(bytes.Slice(i * size, size), operand.IsFloatElement, size));
        }
        if (count > shown) sb.Append(", …");
        sb.Append('}');
        return sb.ToString();
    }

    private static string FormatElement(ReadOnlySpan<byte> bytes, bool isFloat, int size)
    {
        if (isFloat)
        {
            switch (size)
            {
                case 2: return FormatHalf(BinaryPrimitives.ReadHalfLittleEndian(bytes));
                case 4: return FormatSingle(BinaryPrimitives.ReadSingleLittleEndian(bytes));
                case 8: return FormatDouble(BinaryPrimitives.ReadDoubleLittleEndian(bytes));
            }
        }
        return FormatHexValue(bytes);
    }

    private static string FormatSingle(float value)
    {
        if (float.IsNaN(value)) return "NaN_f";
        if (float.IsPositiveInfinity(value)) return "Inf_f";
        if (float.IsNegativeInfinity(value)) return "-Inf_f";
        return value.ToString("R", CultureInfo.InvariantCulture) + "f";
    }

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value)) return "NaN_d";
        if (double.IsPositiveInfinity(value)) return "Inf_d";
        if (double.IsNegativeInfinity(value)) return "-Inf_d";
        return value.ToString("R", CultureInfo.InvariantCulture) + "d";
    }

    private static string FormatHalf(Half value)
    {
        if (Half.IsNaN(value)) return "NaN_f16";
        if (Half.IsPositiveInfinity(value)) return "Inf_f16";
        if (Half.IsNegativeInfinity(value)) return "-Inf_f16";
        return ((float)value).ToString("R", CultureInfo.InvariantCulture) + "f16";
    }

    private static string FormatHexValue(ReadOnlySpan<byte> bytes)
    {
        // 小端字节 → 数值 → MASM 风格十六进制（…h）。至多取 8 字节。
        ulong value = 0;
        int n = bytes.Length < 8 ? bytes.Length : 8;
        for (int i = n - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }
        return value.ToString("X", CultureInfo.InvariantCulture) + "h";
    }

    private static void EnsureMaps(ApplicationAnalysisContext app)
    {
        if (ReferenceEquals(_app, app) && _keyFunctions != null) return;
        Dictionary<ulong, string> map = new();
        try
        {
            object kfa = app.GetOrCreateKeyFunctionAddresses();
            foreach (FieldInfo f in kfa.GetType().GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (f.FieldType == typeof(ulong))
                {
                    ulong v = (ulong)f.GetValue(kfa);
                    if (v != 0) map[v] = f.Name;
                }
            }
        }
        catch { }
        _keyFunctions = map;

        // PE 导出函数表（权威符号源）：VA→名。经反射调用（方法在 PE 专属类型上，非 PE 二进制则优雅降级为空）。
        Dictionary<ulong, string> exports = new();
        try
        {
            object binary = LibCpp2IlMain.Binary;
            System.Type binaryType = binary.GetType();
            binaryType.GetMethod("LoadPeExportTable")?.Invoke(binary, null);
            if (binaryType.GetMethod("GetExportedFunctions")?.Invoke(binary, null) is System.Collections.IEnumerable seq)
            {
                foreach (object entry in seq)
                {
                    if (entry is KeyValuePair<string, ulong> kv && kv.Value != 0 && !exports.ContainsKey(kv.Value))
                    {
                        exports[kv.Value] = kv.Key;
                    }
                }
            }
        }
        catch { }
        _exports = exports;

        _sortedMethodStarts = app.MethodsByAddress.Keys.Where(k => k != 0).OrderBy(k => k).ToArray();
        _globalCache.Clear();
        _app = app;
    }

    private static string Escape(string s)
    {
        if (s.Length > 80) s = s.Substring(0, 80) + "…";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }
}
