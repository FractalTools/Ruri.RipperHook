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
/// <item>解析不到的数据（方法 once-init 标志、RGCTX token）保持原地址。</item>
/// </list>
/// 调用方 <see cref="Il2CppAsmLookup.GetDisassembly"/> 持锁，故静态缓存的读写是串行安全的。
/// </summary>
internal static class Il2CppAsmAnnotator
{
    // MASM 形如 10278DB0h；也兼容 0x10278DB0（ARM 等其它格式化器）。
    private static readonly Regex HexToken =
        new(@"0x(?<a>[0-9A-Fa-f]+)|\b(?<b>[0-9A-Fa-f]+)h\b", RegexOptions.Compiled);

    private static ApplicationAnalysisContext _app;
    private static Dictionary<ulong, string> _keyFunctions;
    private static ulong[] _sortedMethodStarts;
    private static readonly Dictionary<ulong, string> _globalCache = new();

    public static string Annotate(ApplicationAnalysisContext app, string asmText)
    {
        EnsureMaps(app);
        StringBuilder sb = new(asmText.Length + 32);
        foreach (string rawLine in asmText.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            sb.Append(HexToken.Replace(line, m => ReplaceToken(line, m)));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string ReplaceToken(string line, Match m)
    {
        string hex = m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value;
        if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong addr)) return m.Value;
        if (addr < 0x10000) return m.Value; // 小立即数 / 寄存器相对偏移 / 8 位寄存器名 (ah/bh…)
        return Resolve(addr, IsInBrackets(line, m.Index)) ?? m.Value;
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

    private static string Resolve(ulong addr, bool inBrackets)
    {
        if (_app.MethodsByAddress.TryGetValue(addr, out List<MethodAnalysisContext> methods) && methods.Count > 0)
            return methods[0].FullName;
        if (_keyFunctions.TryGetValue(addr, out string keyFunc))
            return keyFunc;
        if (!_globalCache.TryGetValue(addr, out string global))
        {
            global = ResolveGlobal(addr);
            _globalCache[addr] = global;
        }
        if (global != null)
            return global;

        // 未命中元数据：数据引用保持原样（once-init 标志、RGCTX 等无符号信息）。
        if (inBrackets)
            return null;
        // 代码目标：方法体内分支 → loc_；区域外无名运行时函数 → sub_。
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
