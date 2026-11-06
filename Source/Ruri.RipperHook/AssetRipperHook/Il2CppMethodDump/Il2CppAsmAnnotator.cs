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
using Cpp2IL.Core.Utils;

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
    private static Dictionary<ulong, List<string>> _keyFunctions;
    private static Dictionary<ulong, List<string>> _exports;
    private static ulong[] _sortedMethodStarts;
    private static readonly Dictionary<ulong, string> _globalCache = new();
    private static readonly Dictionary<ulong, string> _dataCache = new();
    private static Dictionary<ulong, string> _runtimeGlobals;
    private static ulong _imageBase;
    private static PeSection[] _sections;
    private enum AddressKind { Unknown, Code, ReadOnlyData, WritableData }

    private readonly struct PeSection
    {
        public readonly ulong RvaStart;
        public readonly ulong RvaEnd;
        public readonly ulong FileBackedEnd;
        public readonly bool Executable;
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

    /// <summary>Every address the runtime names itself: what the image exports, what the runtime's own key functions
    /// were found to be, and the globals its start-up code was traced to write.</summary>
    internal static IEnumerable<(ulong Address, string Kind, string Name)> RuntimeNames(ApplicationAnalysisContext app)
    {
        EnsureMaps(app);
        foreach (KeyValuePair<ulong, List<string>> export in _exports)
            foreach (string name in export.Value)
                yield return (export.Key, "export", name);
        foreach (KeyValuePair<ulong, List<string>> function in _keyFunctions)
            foreach (string name in function.Value)
                yield return (function.Key, "keyFunction", name);
        foreach (KeyValuePair<ulong, string> global in _runtimeGlobals)
            yield return (global.Key, "runtimeGlobal", global.Value);
    }

    internal static bool IsKeyFunction(ApplicationAnalysisContext app, ulong address, string nameContains)
    {
        EnsureMaps(app);
        return _keyFunctions.TryGetValue(address, out List<string> names) && names.Any(name => name.Contains(nameContains));
    }

    public static ulong KeyFunctionAddress(ApplicationAnalysisContext app, string nameContains)
    {
        EnsureMaps(app);
        foreach (KeyValuePair<ulong, List<string>> kv in _keyFunctions)
        {
            if (kv.Value.Any(name => name.Contains(nameContains))) return kv.Key;
        }
        return 0;
    }

    internal static bool IsAllocOrRaiseFunction(ApplicationAnalysisContext app, ulong addr)
    {
        EnsureMaps(app);
        if (_keyFunctions.TryGetValue(addr, out List<string> keys))
            return keys.Any(IsAllocOrRaiseName);
        return _exports.TryGetValue(addr, out List<string> names) && names.Any(IsAllocOrRaiseName);
    }

    private static bool IsAllocOrRaiseName(string name)
    {
        return name.Contains("object_new") || name.Contains("bject_new")
            || name.Contains("raise") || name.Contains("Raise")
            || name.Contains("exception") || name.Contains("Exception")
            || name.Contains("throw") || name.Contains("Throw");
    }

    private static string ReplaceToken(string line, Match m, IReadOnlyDictionary<ulong, string> overrides, IReadOnlyDictionary<ulong, DataConstantOperand> dataConstants)
    {
        string hex = m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value;
        if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong addr)) return m.Value;
        if (addr < 0x10000) return m.Value;
        bool inBrackets = IsInBrackets(line, m.Index);
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
            return MethodTargetName(addr, methods);
        if (_exports.TryGetValue(addr, out List<string> exports))
            return FormatAliases(addr, exports, "export");
        if (_keyFunctions.TryGetValue(addr, out List<string> keyFunctions))
            return FormatAliases(addr, keyFunctions, "keyFunction");
        if (Il2CppRuntimeSymbols.NameAt(_app, addr) is { } runtimeSymbol)
            return runtimeSymbol;
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
        if (InsideFunction(addr)) return "loc_" + addr.ToString("X");
        string helper = Il2CppHelperNamer.TryGetName(_app, addr);
        return helper ?? "sub_" + addr.ToString("X");
    }

    /// <summary>Whether the address is somewhere in a function rather than at the first byte of one: the exception
    /// directory says where functions and their chunks begin, so it answers for runtime helpers between methods too;
    /// an image without one is answered by where the methods start.</summary>
    private static bool InsideFunction(ulong addr)
    {
        Il2CppFunctionTable functions = Il2CppFunctionTable.For(_app);
        if (functions.Count == 0) return InMethodBody(addr);
        return !functions.IsPrimaryStart(addr) && functions.TryFind(addr, out _);
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

    internal static string MethodSymbol(MethodAnalysisContext method)
    {
        IReadOnlyList<TypeAnalysisContext> genericArguments = method.GenericParameters;
        if (method is ConcreteGenericMethodAnalysisContext generic)
        {
            genericArguments = generic.MethodGenericParameters.Count > 0
                ? generic.MethodGenericParameters
                : generic.BaseMethodContext.GenericParameters;
        }
        string genericSuffix = genericArguments.Count == 0
            ? string.Empty
            : "<" + string.Join(", ", genericArguments.Select(type => type.FullName)) + ">";
        string name = method.DeclaringType == null
            ? method.DefaultName : method.DeclaringType.FullName + "::" + method.DefaultName;
        return method.ReturnType.FullName + " " + name + genericSuffix
            + "(" + string.Join(", ", method.Parameters.Select(parameter => parameter.ParameterType.FullName)) + ")";
    }

    private static string MethodTargetName(ulong address, List<MethodAnalysisContext> methods)
    {
        MethodAnalysisContext first = methods[0];
        for (int i = 1; i < methods.Count; i++)
        {
            if (!SameMethod(first, methods[i]))
                return "sub_" + address.ToString("X");
        }
        return MethodSymbol(first);
    }

    private static bool SameMethod(MethodAnalysisContext first, MethodAnalysisContext other)
    {
        if (ReferenceEquals(first, other))
            return true;
        if (first is ConcreteGenericMethodAnalysisContext firstGeneric
            && other is ConcreteGenericMethodAnalysisContext otherGeneric)
        {
            return ReferenceEquals(firstGeneric.BaseMethodContext, otherGeneric.BaseMethodContext)
                && SameTypeArguments(firstGeneric.TypeGenericParameters, otherGeneric.TypeGenericParameters)
                && SameTypeArguments(firstGeneric.MethodGenericParameters, otherGeneric.MethodGenericParameters);
        }
        return first.Definition != null && ReferenceEquals(first.Definition, other.Definition)
            && ReferenceEquals(first.DeclaringType, other.DeclaringType);
    }

    private static bool SameTypeArguments(IReadOnlyList<TypeAnalysisContext> first, IReadOnlyList<TypeAnalysisContext> other)
    {
        if (first.Count != other.Count)
            return false;
        for (int i = 0; i < first.Count; i++)
        {
            if (!ReferenceEquals(first[i], other[i]))
                return false;
        }
        return true;
    }

    internal static MetadataUsage MetadataUsageAt(ApplicationAnalysisContext app, ulong address)
    {
        if (!HoldsPointer(app.Binary, address))
            return null;
        if (app.Metadata.MetadataVersion >= 27f && app.Binary.ReadPointerAtVirtualAddress(address) >= uint.MaxValue)
            return null;
        return app.LibCpp2IlContext.GetAnyGlobalByAddress(address);
    }

    private static string ResolveGlobal(ulong addr)
    {
        MetadataUsage usage = MetadataUsageAt(_app, addr);
        if (usage == null)
            return null;
        switch (usage.Type)
        {
            case MetadataUsageType.StringLiteral:
                return "\"" + Escape(usage.AsLiteral()) + "\"";
            case MetadataUsageType.MethodDef:
            case MetadataUsageType.MethodRef:
                MethodAnalysisContext method = _app.ResolveContextForMethod(usage);
                return method == null ? null : MethodSymbol(method) + "_MethodInfo";
            case MetadataUsageType.FieldInfo:
                Il2CppFieldRef fieldReference = _app.Metadata.fieldRefs[usage.RawValue];
                var declaringType = fieldReference.DeclaringType;
                Il2CppTypeDefinition definition = declaringType.CoerceToUnderlyingTypeDefinition();
                Il2CppFieldDefinition field = _app.Metadata.GetFieldDefinitionFromIndex(definition.FirstFieldIdx + fieldReference.fieldIndex);
                TypeAnalysisContext type = _app.ResolveIl2CppType(declaringType);
                return type == null || field?.Name == null ? null : type.FullName + "::" + field.Name + "_FieldInfo";
        }
        string value = usage.Value?.ToString();
        return usage.Type switch
        {
            MetadataUsageType.TypeInfo => value == null ? null : value + "_TypeInfo",
            MetadataUsageType.Type => value == null ? null : value + "_TypeRef",
            MetadataUsageType.FieldRva => value == null ? null : value + "_FieldRva",
            _ => value,
        };
    }

    /// <summary>Whether a pointer-sized global can sit at this address inside the image. A metadata usage slot is
    /// one; asked about an address whose pointer would run off the end of the image, the library reads past it.</summary>
    internal static bool HoldsPointer(Il2CppBinary binary, ulong address) =>
        binary.TryMapVirtualAddressToRaw(address, out long raw) && raw + (binary.is32Bit ? 4 : 8) <= binary.RawLength;

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

    internal static string ReadCString(ApplicationAnalysisContext app, ulong virtualAddress)
    {
        EnsureMaps(app);
        return TryReadCString(virtualAddress);
    }

    private static string TryReadCString(ulong virtualAddress)
    {
        Il2CppBinary binary = _app.Binary;
        if (!binary.TryMapVirtualAddressToRaw(virtualAddress, out long raw))
            return null;
        if (raw < 0 || raw >= binary.RawLength)
            return null;

        StringBuilder sb = new();
        for (int i = 0; i < 256; i++)
        {
            if (raw + i >= binary.RawLength)
                return null;
            byte ch = binary.GetByteAtRawAddress((ulong)(raw + i));
            if (ch == 0)
                break;
            if (ch < 0x20 || ch > 0x7E)
                return null;
            sb.Append((char)ch);
        }
        return sb.Length >= 2 ? sb.ToString() : null;
    }

    private static string TryResolveCodePointer(ulong slotAddress)
    {
        Il2CppBinary binary = _app.Binary;
        if (binary.is32Bit)
            return null;
        if (!binary.TryMapVirtualAddressToRaw(slotAddress, out long raw))
            return null;
        if (raw < 0 || raw + 8 > binary.RawLength)
            return null;

        ulong target = 0;
        for (int i = 7; i >= 0; i--)
            target = (target << 8) | binary.GetByteAtRawAddress((ulong)(raw + i));
        if (target < 0x10000 || ClassifyAddress(target, out _) != AddressKind.Code)
            return null;

        if (_app.MethodsByAddress.TryGetValue(target, out List<MethodAnalysisContext> targetMethods) && targetMethods.Count > 0)
            return "->" + MethodTargetName(target, targetMethods);
        if (_exports.TryGetValue(target, out List<string> targetExports))
            return "->" + FormatAliases(target, targetExports, "export");
        if (_keyFunctions.TryGetValue(target, out List<string> targetKeys))
            return "->" + FormatAliases(target, targetKeys, "keyFunction");
        if (Il2CppRuntimeSymbols.NameAt(_app, target) is { } targetRuntime)
            return "->" + targetRuntime;
        return "->" + (InMethodBody(target) ? "loc_" : "sub_") + target.ToString("X");
    }

    private static string TryReadDataConstant(ulong virtualAddress, in DataConstantOperand operand)
    {
        Il2CppBinary binary = _app.Binary;
        int total = operand.ElementSize * operand.ElementCount;
        if (total <= 0 || total > 64)            return null;

        if (!binary.TryMapVirtualAddressToRaw(virtualAddress, out long raw))
            return null;
        if (raw < 0 || raw + total > binary.RawLength)
            return null;

        Span<byte> buffer = stackalloc byte[64];
        Span<byte> bytes = buffer.Slice(0, total);
        for (int i = 0; i < total; i++)
        {
            bytes[i] = binary.GetByteAtRawAddress((ulong)(raw + i));
        }

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

    private static void ParsePeSections(ApplicationAnalysisContext app)
    {
        _sections = null;
        _imageBase = 0;
        Il2CppBinary binary = app.Binary;

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
        if (optional + 32 > headerLength) return;
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
            bool executable = (characteristics & 0x20000000u) != 0;
            bool writable = (characteristics & 0x80000000u) != 0;
            parsed[i] = new PeSection(virtualAddress, virtualAddress + virtualSize, virtualAddress + rawSize, executable, writable);
        }
        _sections = parsed;
    }

    private static void EnsureMaps(ApplicationAnalysisContext app)
    {
        if (ReferenceEquals(_app, app) && _keyFunctions != null) return;
        Dictionary<ulong, List<string>> map = new();
        object kfa = app.GetOrCreateKeyFunctionAddresses();
        foreach (FieldInfo f in kfa.GetType().GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
        {
            if (f.FieldType == typeof(ulong))
            {
                ulong v = (ulong)f.GetValue(kfa);
                if (v != 0)
                {
                    if (!map.TryGetValue(v, out List<string> names))
                        map[v] = names = new List<string>();
                    if (!names.Contains(f.Name))
                        names.Add(f.Name);
                }
            }
        }
        _keyFunctions = map;

        Dictionary<ulong, List<string>> exports = new();
        foreach (KeyValuePair<string, ulong> kv in app.Binary.GetExportedFunctions())
        {
            if (kv.Value != 0)
            {
                if (!exports.TryGetValue(kv.Value, out List<string> names))
                    exports[kv.Value] = names = new List<string>();
                if (!names.Contains(kv.Key))
                    names.Add(kv.Key);
            }
        }
        _exports = exports;

        _sortedMethodStarts = app.MethodsByAddress.Keys.Where(k => k != 0).OrderBy(k => k).ToArray();
        _globalCache.Clear();
        _dataCache.Clear();
        ParsePeSections(app);
        _runtimeGlobals = Il2CppX86Listing.TraceRuntimeGlobals(app);
        _app = app;
    }

    private static string FormatAliases(ulong address, IReadOnlyList<string> names, string kind) => names.Count == 1
        ? names[0] : "sub_" + address.ToString("X") + "{" + kind + " aliases=" + string.Join(" | ", names) + "}";

    private static string Escape(string s)
    {
        if (s.Length > 80) s = s.Substring(0, 80) + "…";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }
}
