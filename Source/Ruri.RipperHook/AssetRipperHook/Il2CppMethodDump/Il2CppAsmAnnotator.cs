using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using LibCpp2IL;
using LibCpp2IL.Metadata;
using Cpp2IL.Core.Model.Contexts;

namespace Ruri.RipperHook.AR;

internal static class Il2CppAsmAnnotator
{
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

    private static readonly Regex HexToken =
        new(@"0x(?<a>[0-9A-Fa-f]+)|\b(?<b>[0-9A-Fa-f]+)h\b", RegexOptions.Compiled);

    private static ApplicationAnalysisContext _app;
    private static Dictionary<ulong, string> _keyFunctions;
    private static Dictionary<ulong, string> _exports;    private static ulong[] _sortedMethodStarts;
    private static readonly Dictionary<ulong, string> _globalCache = new();
    private static readonly Dictionary<ulong, string> _dataCache = new();    private static Dictionary<ulong, string> _runtimeGlobals;    private static ulong _imageBase;
    private static PeSection[] _sections;
    private enum AddressKind { Unknown, Code, ReadOnlyData, WritableData }

    private readonly struct PeSection
    {
        public readonly ulong RvaStart;
        public readonly ulong RvaEnd;        public readonly ulong FileBackedEnd;        public readonly bool Executable;
        public readonly bool Writable;

        public PeSection(ulong rvaStart, ulong rvaEnd, ulong fileBackedEnd, bool executable, bool writable)
        {
            RvaStart = rvaStart;
            RvaEnd = rvaEnd;
            FileBackedEnd = fileBackedEnd;
            Executable = executable;
            Writable = writable;
        }
    }

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

    public static string AnnotateLine(ApplicationAnalysisContext app, string line, IReadOnlyDictionary<ulong, string> overrides = null, IReadOnlyDictionary<ulong, DataConstantOperand> dataConstants = null)
    {
        EnsureMaps(app);
        return HexToken.Replace(line, m => ReplaceToken(line, m, overrides, dataConstants));
    }

    internal static string ResolveAddress(ApplicationAnalysisContext app, ulong address, bool inBrackets,
        IReadOnlyDictionary<ulong, string> overrides, IReadOnlyDictionary<ulong, DataConstantOperand> dataConstants)
    {
        if (address < 0x10000) return null;
        EnsureMaps(app);
        return Resolve(address, inBrackets, overrides, dataConstants);
    }

    public static ulong KeyFunctionAddress(ApplicationAnalysisContext app, string nameContains)
    {
        EnsureMaps(app);
        foreach (KeyValuePair<ulong, string> kv in _keyFunctions)
        {
            if (kv.Value.Contains(nameContains)) return kv.Key;
        }
        return 0;
    }

    internal static bool IsAllocOrRaiseFunction(ApplicationAnalysisContext app, ulong addr)
    {
        EnsureMaps(app);
        string name = null;
        if (_keyFunctions != null && _keyFunctions.TryGetValue(addr, out string k)) name = k;
        else if (_exports != null && _exports.TryGetValue(addr, out string e)) name = e;
        if (name == null) return false;
        return name.Contains("object_new") || name.Contains("bject_new")
            || name.Contains("raise") || name.Contains("Raise")
            || name.Contains("exception") || name.Contains("Exception")
            || name.Contains("throw") || name.Contains("Throw");
    }

    private static string ReplaceToken(string line, Match m, IReadOnlyDictionary<ulong, string> overrides, IReadOnlyDictionary<ulong, DataConstantOperand> dataConstants)
    {
        string hex = m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value;
        if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong addr)) return m.Value;
        if (addr < 0x10000) return m.Value;        bool inBrackets = IsInBrackets(line, m.Index);
        if (inBrackets && IsRegisterRelativeDisplacement(line, m.Index)) return m.Value;
        return Resolve(addr, inBrackets, overrides, dataConstants) ?? m.Value;
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

    private static bool IsRegisterRelativeDisplacement(string line, int tokenIndex)
    {
        int open = line.LastIndexOf('[', System.Math.Min(tokenIndex, line.Length - 1));
        if (open < 0) return false;
        int close = line.IndexOf(']', tokenIndex);
        if (close < 0) close = line.Length;
        for (int i = open + 1; i < close && i < line.Length; i++)
        {
            char c = line[i];
            if (c == '+' || c == '*') return true;
        }
        return false;
    }

    private static string Resolve(ulong addr, bool inBrackets, IReadOnlyDictionary<ulong, string> overrides, IReadOnlyDictionary<ulong, DataConstantOperand> dataConstants)
    {
        if (overrides != null && overrides.TryGetValue(addr, out string ov))
            return ov;
        if (_app.MethodsByAddress.TryGetValue(addr, out List<MethodAnalysisContext> methods) && methods.Count > 0)
            return MethodTargetName(methods);
        if (_exports.TryGetValue(addr, out string export))            return export;
        if (_keyFunctions.TryGetValue(addr, out string keyFunc))
            return keyFunc;
        if (_runtimeGlobals != null && _runtimeGlobals.TryGetValue(addr, out string runtimeGlobal))            return runtimeGlobal;
        if (!_globalCache.TryGetValue(addr, out string global))
        {
            global = ResolveGlobal(addr);
            _globalCache[addr] = global;
        }
        if (global != null)
            return global;

        if (inBrackets)
        {
            if (dataConstants != null && dataConstants.TryGetValue(addr, out DataConstantOperand operand) && ConstantAddressAllowed(addr, operand))
            {
                string constant = TryReadDataConstant(addr, operand);
                if (constant != null)
                    return constant;
            }
            if (!_dataCache.TryGetValue(addr, out string dataSymbol))
            {
                dataSymbol = ResolveDataAddress(addr);
                _dataCache[addr] = dataSymbol;
            }
            return dataSymbol;
        }
        return CodeLabel(addr);
    }

    private static string CodeLabel(ulong addr)
    {
        if (InMethodBody(addr)) return "loc_" + addr.ToString("X");
        string helper = Il2CppHelperNamer.TryGetName(_app, addr);
        return helper ?? "sub_" + addr.ToString("X");
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
        return best >= 0 && best + 1 < starts.Length && addr > starts[best] && addr < starts[best + 1];
    }

    private static string MethodTargetName(List<MethodAnalysisContext> methods)
    {
        if (methods.Count == 1)
            return methods[0].FullName;
        string name = methods[0].FullName;
        bool allSame = true;
        for (int i = 1; i < methods.Count; i++)
            if (methods[i].FullName != name) { allSame = false; break; }
        if (allSame)
            return name;
        string open = StripGenericArguments(name);
        bool allSameOpen = true;
        for (int i = 1; i < methods.Count; i++)
            if (StripGenericArguments(methods[i].FullName) != open) { allSameOpen = false; break; }
        if (allSameOpen)
            return open;

        string member = CommonMemberName(methods);
        return member ?? open;
    }

    private static string CommonMemberName(List<MethodAnalysisContext> methods)
    {
        static string Member(string fullName)
        {
            int sep = fullName.LastIndexOf("::", System.StringComparison.Ordinal);
            return sep >= 0 ? fullName.Substring(sep + 2) : fullName;
        }
        string first = Member(methods[0].FullName);
        for (int i = 1; i < methods.Count; i++)
            if (Member(methods[i].FullName) != first)
                return null;
        return first;
    }

    private static string StripGenericArguments(string fullName)
    {
        if (fullName.IndexOf('<') < 0)
            return fullName;
        StringBuilder sb = new(fullName.Length);
        int depth = 0;
        foreach (char c in fullName)
        {
            if (c == '<') depth++;
            else if (c == '>') { if (depth > 0) depth--; }
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
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
            if (usage != null)
            {
                switch (usage.Type)
                {
                    case MetadataUsageType.MethodDef:
                        string methodKey = usage.AsMethod()?.GlobalKey;
                        if (methodKey != null) return methodKey;
                        break;
                    case MetadataUsageType.MethodRef:
                        string genericKey = usage.AsGenericMethodRef()?.ToString();
                        if (genericKey != null) return genericKey;
                        break;
                    case MetadataUsageType.FieldInfo:
                        Il2CppFieldDefinition field = usage.AsField();
                        if (field?.DeclaringType != null && field.Name != null)
                            return field.DeclaringType.Name + "::" + field.Name;
                        break;
                }
                if (usage.Value != null)
                {
                    string value = usage.Value.ToString();
                    return usage.Type.ToString().Contains("Type") ? value + "_TypeInfo" : value;
                }
            }
        }
        catch { }
        return null;
    }

    private static string ResolveDataAddress(ulong addr)
    {
        if (_imageBase != 0 && addr == _imageBase)
            return "image_base";
        AddressKind kind = ClassifyAddress(addr, out bool fileBacked);
        if (kind == AddressKind.Code)
            return CodeLabel(addr);
        if (kind == AddressKind.ReadOnlyData && fileBacked)
        {
            string cString = TryReadCString(addr);
            if (cString != null)
                return "\"" + Escape(cString) + "\"";
        }
        if (fileBacked)
        {
            string codePointer = TryResolveCodePointer(addr);
            if (codePointer != null)
                return codePointer;
        }
        return "g_" + addr.ToString("X");
    }

    internal static string ReadCString(ulong virtualAddress) => TryReadCString(virtualAddress);

    private static string TryReadCString(ulong virtualAddress)
    {
        Il2CppBinary binary = LibCpp2IlMain.Binary;
        if (binary == null)
            return null;
        long raw;
        try
        {
            if (!binary.TryMapVirtualAddressToRaw(virtualAddress, out raw))
                return null;
        }
        catch { return null; }
        if (raw < 0 || raw >= binary.RawLength)
            return null;

        StringBuilder sb = new();
        for (int i = 0; i < 256; i++)
        {
            if (raw + i >= binary.RawLength)
                return null;
            byte ch;
            try { ch = binary.GetByteAtRawAddress((ulong)(raw + i)); }
            catch { return null; }
            if (ch == 0)
                break;
            if (ch < 0x20 || ch > 0x7E)
                return null;            sb.Append((char)ch);
        }
        return sb.Length >= 2 ? sb.ToString() : null;
    }

    private static string TryResolveCodePointer(ulong slotAddress)
    {
        Il2CppBinary binary = LibCpp2IlMain.Binary;
        if (binary == null || binary.is32Bit)
            return null;
        long raw;
        try
        {
            if (!binary.TryMapVirtualAddressToRaw(slotAddress, out raw))
                return null;
        }
        catch { return null; }
        if (raw < 0 || raw + 8 > binary.RawLength)
            return null;

        ulong target = 0;
        try
        {
            for (int i = 7; i >= 0; i--)
                target = (target << 8) | binary.GetByteAtRawAddress((ulong)(raw + i));
        }
        catch { return null; }
        if (target < 0x10000 || ClassifyAddress(target, out _) != AddressKind.Code)
            return null;

        if (_app.MethodsByAddress.TryGetValue(target, out List<MethodAnalysisContext> targetMethods) && targetMethods.Count > 0)
            return "->" + MethodTargetName(targetMethods);
        if (_exports.TryGetValue(target, out string targetExport))
            return "->" + targetExport;
        if (_keyFunctions.TryGetValue(target, out string targetKeyFunc))
            return "->" + targetKeyFunc;
        return "->" + (InMethodBody(target) ? "loc_" : "sub_") + target.ToString("X");
    }

    private static string TryReadDataConstant(ulong virtualAddress, in DataConstantOperand operand)
    {
        Il2CppBinary binary = LibCpp2IlMain.Binary;
        if (binary == null)
            return null;

        int total = operand.ElementSize * operand.ElementCount;
        if (total <= 0 || total > 64)            return null;

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
        ulong value = 0;
        int n = bytes.Length < 8 ? bytes.Length : 8;
        for (int i = n - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }
        return value.ToString("X", CultureInfo.InvariantCulture) + "h";
    }

    private static bool ConstantAddressAllowed(ulong addr, in DataConstantOperand operand)
    {
        if (_sections == null)
            return !(operand.ElementCount == 1 && !operand.IsFloatElement);
        return ClassifyAddress(addr, out bool fileBacked) == AddressKind.ReadOnlyData && fileBacked;
    }

    private static AddressKind ClassifyAddress(ulong virtualAddress, out bool fileBacked)
    {
        fileBacked = false;
        PeSection[] sections = _sections;
        if (sections == null || virtualAddress < _imageBase)
            return AddressKind.Unknown;

        ulong rva = virtualAddress - _imageBase;
        foreach (PeSection section in sections)
        {
            if (rva >= section.RvaStart && rva < section.RvaEnd)
            {
                fileBacked = rva < section.FileBackedEnd;
                if (section.Executable) return AddressKind.Code;
                return section.Writable ? AddressKind.WritableData : AddressKind.ReadOnlyData;
            }
        }
        return AddressKind.Unknown;
    }

    private static void ParsePeSections()
    {
        _sections = null;
        _imageBase = 0;
        try
        {
            Il2CppBinary binary = LibCpp2IlMain.Binary;
            if (binary == null) return;

            long rawLength = binary.RawLength;
            int headerLength = (int)System.Math.Min(rawLength, 16384L);
            if (headerLength < 0x200) return;

            Span<byte> header = stackalloc byte[16384];
            header = header.Slice(0, headerLength);
            for (int i = 0; i < headerLength; i++)
            {
                header[i] = binary.GetByteAtRawAddress((ulong)i);
            }

            if (header[0] != (byte)'M' || header[1] != (byte)'Z') return;
            int pe = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(0x3C));
            if (pe <= 0 || pe + 0x18 > headerLength) return;
            if (header[pe] != (byte)'P' || header[pe + 1] != (byte)'E') return;

            int coff = pe + 4;
            ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(coff + 2));
            ushort optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(coff + 16));
            int optional = coff + 20;
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(optional));
            _imageBase = magic == 0x20b
                ? BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(optional + 24))                : BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(optional + 28));
            int tableStart = optional + optionalSize;
            if (sectionCount == 0 || sectionCount > 96 || tableStart + sectionCount * 40 > headerLength) return;

            PeSection[] parsed = new PeSection[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                int s = tableStart + i * 40;
                uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(s + 8));
                uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(s + 12));
                uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(s + 16));
                uint characteristics = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(s + 36));
                bool executable = (characteristics & 0x20000000u) != 0;                bool writable = (characteristics & 0x80000000u) != 0;                parsed[i] = new PeSection(virtualAddress, virtualAddress + virtualSize, virtualAddress + rawSize, executable, writable);
            }
            _sections = parsed;
        }
        catch
        {
            _sections = null;
        }
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
        _dataCache.Clear();
        ParsePeSections();
        _runtimeGlobals = Il2CppX86Listing.TraceRuntimeGlobals(app);
        _app = app;
    }

    private static string Escape(string s)
    {
        if (s.Length > 80) s = s.Substring(0, 80) + "…";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }
}
