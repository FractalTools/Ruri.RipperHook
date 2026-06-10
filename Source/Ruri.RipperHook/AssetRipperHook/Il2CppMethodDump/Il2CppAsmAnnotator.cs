using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 把 PrintAssembly 文本里的裸地址翻译成符号（全部来自 GlobalMetadata + 二进制符号信息）：
/// 调用目标 → 托管方法名 / il2cpp 运行时函数名；数据全局 → TypeInfo / 字符串字面量 / 方法 / 字段。
/// 以行尾 <c>; 符号</c> 注释追加，**保留原地址**便于和 IDA/Ghidra 交叉引用。解析不到的
/// （方法 once-init 标志、RGCTX token、函数内分支目标）原样保留。
/// 调用方 <see cref="Il2CppAsmLookup.GetDisassembly"/> 持锁，故此处静态缓存的读写是串行安全的。
/// </summary>
internal static class Il2CppAsmAnnotator
{
    // MASM 形如 10278DB0h；也兼容 0x10278DB0（ARM 等其它格式化器）。
    private static readonly Regex HexToken =
        new(@"0x(?<a>[0-9A-Fa-f]+)|\b(?<b>[0-9A-Fa-f]+)h\b", RegexOptions.Compiled);

    private static ApplicationAnalysisContext _app;
    private static Dictionary<ulong, string> _keyFunctions;
    private static readonly Dictionary<ulong, string> _globalCache = new();

    public static string Annotate(ApplicationAnalysisContext app, string asmText)
    {
        EnsureKeyFunctions(app);
        StringBuilder sb = new(asmText.Length + 64);
        foreach (string rawLine in asmText.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            List<string> symbols = null;
            HashSet<ulong> seen = null;
            foreach (Match m in HexToken.Matches(line))
            {
                string hex = m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value;
                if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong addr)) continue;
                if (addr < 0x10000) continue; // 小立即数 / 寄存器相对偏移 / 8 位寄存器名 (ah/bh…)
                seen ??= new HashSet<ulong>();
                if (!seen.Add(addr)) continue;
                string symbol = Resolve(app, addr);
                if (symbol != null) (symbols ??= new List<string>()).Add(symbol);
            }
            sb.Append(line);
            if (symbols != null) sb.Append("  ; ").Append(string.Join(", ", symbols));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void EnsureKeyFunctions(ApplicationAnalysisContext app)
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
        _globalCache.Clear();
        _app = app;
    }

    private static string Resolve(ApplicationAnalysisContext app, ulong addr)
    {
        if (app.MethodsByAddress.TryGetValue(addr, out List<MethodAnalysisContext> methods) && methods.Count > 0)
            return methods[0].FullName;
        if (_keyFunctions.TryGetValue(addr, out string keyFunc))
            return keyFunc;
        if (_globalCache.TryGetValue(addr, out string cached))
            return cached;
        string resolved = ResolveGlobal(addr);
        _globalCache[addr] = resolved;
        return resolved;
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
                return usage.Type.ToString().Contains("Type") ? value + " (TypeInfo)" : value;
            }
        }
        catch { }
        return null;
    }

    private static string Escape(string s)
    {
        if (s.Length > 80) s = s.Substring(0, 80) + "…";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }
}
