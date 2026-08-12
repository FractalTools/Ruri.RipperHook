extern alias icedreal;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;
using icedreal::Iced.Intel;

namespace Ruri.RipperHook.AR;

internal sealed class Il2CppTypeModel
{
    private static Il2CppTypeModel _cached;
    private static ApplicationAnalysisContext _cachedApp;

    private readonly ApplicationAnalysisContext _app;

    private readonly Dictionary<Il2CppTypeDefinition, TypeAnalysisContext> _byDefinition = new();
    private readonly Dictionary<TypeAnalysisContext, Dictionary<int, FieldAnalysisContext>> _instanceFields = new();
    private readonly Dictionary<TypeAnalysisContext, Dictionary<int, FieldAnalysisContext>> _staticFields = new();

    public readonly HashSet<(string, int)> CondemnedVtableSlots = new();

    public readonly HashSet<(string, int)> CondemnedVtableMethods = new();

    private readonly Dictionary<TypeAnalysisContext, string[]> _vtableNames = new();    private readonly Dictionary<TypeAnalysisContext, TypeAnalysisContext[]> _vtableReturns = new();    private readonly Dictionary<TypeAnalysisContext, byte[]> _vtableReturnKinds = new();    private readonly Dictionary<TypeAnalysisContext, sbyte[]> _vtableParamCounts = new();    private readonly Dictionary<TypeAnalysisContext, TypeAnalysisContext[][]> _vtableParamTypes = new();
    public const byte ReturnKindUnresolved = 0;
    public const byte ReturnKindVoid = 1;
    public const byte ReturnKindScalarInt = 2;
    public const byte ReturnKindStruct = 3;
    public const byte ReturnKindRef = 4;
    public const byte ReturnKindPointer = 5;
    public const byte ReturnKindScalarFloat = 6;
    public const byte ReturnKindBool = 7;
    private static readonly HashSet<string> _scalarIntPrimitives = new()
    {
        "System.Byte", "System.SByte", "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Char",
    };

    public int StaticFieldsOffset { get; private set; } = -1;

    public int VtableOffset { get; private set; } = -1;

    public static Il2CppTypeModel Get(ApplicationAnalysisContext app)
    {
        if (ReferenceEquals(_cachedApp, app) && _cached != null)
            return _cached;
        Il2CppTypeModel model = new(app);
        _cached = model;
        _cachedApp = app;
        return model;
    }

    private Il2CppTypeModel(ApplicationAnalysisContext app)
    {
        _app = app;
        foreach (AssemblyAnalysisContext assembly in app.Assemblies)
        {
            foreach (TypeAnalysisContext type in assembly.Types)
            {
                if (type?.Definition != null)
                    _byDefinition[type.Definition] = type;
            }
        }
        StaticFieldsOffset = DiscoverStaticFieldsOffset(app);
        VtableOffset = DiscoverVtableOffset(app);
    }

    public bool TryGetTypeForTypeInfoGlobal(ulong globalAddress, out TypeAnalysisContext type)
    {
        type = null;
        try
        {
            MetadataUsage usage = LibCpp2IlMain.GetAnyGlobalByAddress(globalAddress);
            if (usage == null)
                return false;
            if (usage.Type != MetadataUsageType.TypeInfo && usage.Type != MetadataUsageType.Type)
                return false;
            Il2CppTypeDefinition definition = usage.AsType()?.baseType;
            if (definition == null)
                return false;
            return _byDefinition.TryGetValue(definition, out type);
        }
        catch
        {
            return false;
        }
    }

    public bool IsReturnedViaHiddenPointer(TypeAnalysisContext returnType)
    {
        if (returnType == null || !returnType.IsValueType || returnType.IsEnumType)
            return false;
        int size = EstimateValueTypeSize(returnType, 0);
        return size is not (1 or 2 or 4 or 8);
    }

    private int EstimateValueTypeSize(TypeAnalysisContext type, int depth)
    {
        if (type == null || !type.IsValueType || depth > 6)
            return 8;
        if (type.Definition == null)
            return type is GenericInstanceTypeAnalysisContext generic ? EstimateGenericValueTypeSize(generic, depth) : 8;
        int max = 0;
        foreach (FieldAnalysisContext field in type.Fields)
        {
            if (field.IsStatic)
                continue;
            int offset;
            try { offset = field.Offset; }
            catch { continue; }
            if (offset < 0)
                continue;
            int end = offset + PrimitiveSize(field.FieldType, depth + 1);
            if (end > max) max = end;
        }
        return max == 0 ? 1 : max;
    }

    private int EstimateGenericValueTypeSize(GenericInstanceTypeAnalysisContext generic, int depth)
    {
        TypeAnalysisContext open = generic.GenericType;
        if (open?.Definition == null || depth > 6)
            return 8;
        Dictionary<string, TypeAnalysisContext> subst = new();
        var parameters = open.GenericParameters;
        var arguments = generic.GenericArguments;
        for (int i = 0; i < parameters.Count && i < arguments.Count; i++)
            if (parameters[i]?.Name != null)
                subst[parameters[i].Name] = arguments[i];
        int offset = 0, maxAlign = 1;
        foreach (FieldAnalysisContext field in open.Fields)
        {
            if (field.IsStatic)
                continue;
            TypeAnalysisContext fieldType = field.FieldType;
            if (fieldType != null && fieldType.Type is Il2CppTypeEnum.IL2CPP_TYPE_VAR or Il2CppTypeEnum.IL2CPP_TYPE_MVAR
                && fieldType.Name != null && subst.TryGetValue(fieldType.Name, out TypeAnalysisContext concrete))
                fieldType = concrete;
            int size = PrimitiveSize(fieldType, depth + 1);
            int align = size < 1 ? 1 : System.Math.Min(size, 8);
            offset = (offset + align - 1) & ~(align - 1);            offset += size;
            if (align > maxAlign) maxAlign = align;
        }
        return offset == 0 ? 1 : (offset + maxAlign - 1) & ~(maxAlign - 1);
    }

    private int PrimitiveSize(TypeAnalysisContext type, int depth)
    {
        if (type == null)
            return 8;
        switch (type.Type)
        {
            case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
            case Il2CppTypeEnum.IL2CPP_TYPE_I1:
            case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                return 1;
            case Il2CppTypeEnum.IL2CPP_TYPE_I2:
            case Il2CppTypeEnum.IL2CPP_TYPE_U2:
            case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                return 2;
            case Il2CppTypeEnum.IL2CPP_TYPE_I4:
            case Il2CppTypeEnum.IL2CPP_TYPE_U4:
            case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                return 4;
            case Il2CppTypeEnum.IL2CPP_TYPE_I8:
            case Il2CppTypeEnum.IL2CPP_TYPE_U8:
            case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                return 8;
            case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                return EstimateValueTypeSize(type, depth);            default:
                return 8;        }
    }

    public bool TryGetInstanceField(TypeAnalysisContext type, int offset, out FieldAnalysisContext field)
        => GetOffsetMap(_instanceFields, type, statics: false).TryGetValue(offset, out field);

    public bool TryGetStaticField(TypeAnalysisContext type, int offset, out FieldAnalysisContext field)
        => GetOffsetMap(_staticFields, type, statics: true).TryGetValue(offset, out field);

    private static TypeAnalysisContext Unwrap(TypeAnalysisContext type)
        => type is GenericInstanceTypeAnalysisContext generic && generic.GenericType?.IsValueType == false
            ? generic.GenericType
            : type;

    private Dictionary<int, FieldAnalysisContext> GetOffsetMap(
        Dictionary<TypeAnalysisContext, Dictionary<int, FieldAnalysisContext>> cache, TypeAnalysisContext type, bool statics)
    {
        type = Unwrap(type);
        if (cache.TryGetValue(type, out Dictionary<int, FieldAnalysisContext> map))
            return map;

        map = new Dictionary<int, FieldAnalysisContext>();
        if (statics)
        {
            AddFields(map, type, statics: true);
        }
        else
        {
            TypeAnalysisContext current = type;
            int guard = 0;
            while (current != null && guard++ < 64)
            {
                AddFields(map, current, statics: false);
                current = Unwrap(current.BaseType);
            }
        }
        cache[type] = map;
        return map;
    }

    private static void AddFields(Dictionary<int, FieldAnalysisContext> map, TypeAnalysisContext type, bool statics)
    {
        if (type.Definition == null)
            return;
        bool allowZeroOffset = statics || type.IsValueType;        foreach (FieldAnalysisContext field in type.Fields)
        {
            if (field.IsStatic != statics)
                continue;
            if ((field.Attributes & System.Reflection.FieldAttributes.Literal) != 0)
                continue;
            int offset;
            try { offset = field.Offset; }
            catch { continue; }
            if (offset < 0 || (offset == 0 && !allowZeroOffset))
                continue;
            map.TryAdd(offset, field);        }
    }

    private int DiscoverStaticFieldsOffset(ApplicationAnalysisContext app)
    {
        if (app.Binary is not PE || app.Binary.is32Bit)
            return -1;

        Dictionary<int, int> confirmed = new();
        int scanned = 0;
        int confirmations = 0;

        foreach (AssemblyAnalysisContext assembly in app.Assemblies)
        {
            foreach (TypeAnalysisContext type in assembly.Types)
            {
                foreach (MethodAnalysisContext method in type.Methods)
                {
                    if (method.UnderlyingPointer == 0)
                        continue;
                    if (scanned >= 4000 || confirmations >= 400)
                        goto done;
                    scanned++;
                    ScanMethodForStaticIdiom(method, confirmed, ref confirmations);
                }
            }
        }

    done:
        int best = -1;
        int bestCount = 0;
        foreach (KeyValuePair<int, int> candidate in confirmed)
        {
            if (candidate.Value > bestCount)
            {
                bestCount = candidate.Value;
                best = candidate.Key;
            }
        }
        return bestCount >= 3 ? best : -1;    }

    private void ScanMethodForStaticIdiom(MethodAnalysisContext method, Dictionary<int, int> confirmed, ref int confirmations)
    {
        byte[] bytes;
        try
        {
            method.EnsureRawBytes();
            bytes = method.RawBytes.ToArray();
        }
        catch { return; }
        if (bytes.Length == 0 || bytes.Length > 0x4000)
            return;

        Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), method.UnderlyingPointer);
        ulong end = method.UnderlyingPointer + (ulong)bytes.Length;

        TypeAnalysisContext[] typeInfoOf = new TypeAnalysisContext[16];
        TypeAnalysisContext[] staticBaseType = new TypeAnalysisContext[16];
        int[] staticBaseCandidateC = new int[16];
        for (int i = 0; i < 16; i++) staticBaseCandidateC[i] = -1;

        int guard = 0;
        while (decoder.IP < end && guard++ < 8000)
        {
            decoder.Decode(out Instruction insn);
            if (insn.IsInvalid)
                break;
            if (insn.Mnemonic != Mnemonic.Mov || insn.Op0Kind != OpKind.Register || insn.Op1Kind != OpKind.Memory)
                continue;

            int dst = RegisterFlowUtil.GpIndex(insn.Op0Register);
            if (dst < 0)
                continue;

            int baseIndex = RegisterFlowUtil.GpIndex(insn.MemoryBase);
            if (baseIndex >= 0 && insn.MemoryIndex == Register.None)
            {
                int disp = (int)insn.MemoryDisplacement64;
                if (staticBaseType[baseIndex] != null && staticBaseCandidateC[baseIndex] >= 0
                    && TryGetStaticField(staticBaseType[baseIndex], disp, out _))
                {
                    int c = staticBaseCandidateC[baseIndex];
                    confirmed[c] = confirmed.TryGetValue(c, out int n) ? n + 1 : 1;
                    confirmations++;
                }
            }

            typeInfoOf[dst] = null;
            staticBaseType[dst] = null;
            staticBaseCandidateC[dst] = -1;

            if (insn.IsIPRelativeMemoryOperand || (insn.MemoryBase == Register.None && insn.MemoryIndex == Register.None))
            {
                if (TryGetTypeForTypeInfoGlobal(insn.MemoryDisplacement64, out TypeAnalysisContext infoType))
                    typeInfoOf[dst] = infoType;
            }
            else if (baseIndex >= 0 && insn.MemoryIndex == Register.None && typeInfoOf[baseIndex] != null)
            {
                staticBaseType[dst] = typeInfoOf[baseIndex];
                staticBaseCandidateC[dst] = (int)insn.MemoryDisplacement64;
            }
        }
    }

    public bool TryGetVirtualMethodName(TypeAnalysisContext type, int byteOffset, out string name)
    {
        name = null;
        if (VtableOffset < 0 || type?.Definition == null)
            return false;
        int slot = VtableSlotFromOffset(byteOffset);
        if (slot < 0)
            return false;
        string[] names = GetVtableNames(type);
        if (slot >= names.Length)
            return false;
        name = names[slot];
        return name != null;
    }

    private int VtableSlotFromOffset(int byteOffset)
    {
        int offsetInVtable = byteOffset - VtableOffset;
        if (offsetInVtable < 0)
            return -1;
        if (offsetInVtable % 0x10 != 0 && offsetInVtable % 8 == 0)
            offsetInVtable -= 8;        if (offsetInVtable < 0 || offsetInVtable % 0x10 != 0)
            return -1;
        return offsetInVtable / 0x10;
    }

    public bool TryGetVirtualReturnType(TypeAnalysisContext type, int byteOffset, out TypeAnalysisContext returnType)
    {
        returnType = null;
        if (VtableOffset < 0 || type?.Definition == null)
            return false;
        int slot = VtableSlotFromOffset(byteOffset);
        if (slot < 0)
            return false;
        EnsureVtable(type);
        TypeAnalysisContext[] returns = _vtableReturns[type];
        if (slot >= returns.Length)
            return false;
        returnType = returns[slot];
        return returnType != null;
    }

    public byte GetVirtualReturnKind(TypeAnalysisContext type, int byteOffset)
    {
        if (VtableOffset < 0 || type?.Definition == null)
            return ReturnKindUnresolved;
        int slot = VtableSlotFromOffset(byteOffset);
        if (slot < 0)
            return ReturnKindUnresolved;
        EnsureVtable(type);
        byte[] kinds = _vtableReturnKinds[type];
        return slot < kinds.Length ? kinds[slot] : ReturnKindUnresolved;
    }

    public byte ClassifyReturnKind(TypeAnalysisContext t) => ClassifyReturn(t);

    private static byte ClassifyReturn(TypeAnalysisContext t)
    {
        if (t == null)
            return ReturnKindUnresolved;
        string fullName = t.FullName;
        if (fullName == "System.Void")
            return ReturnKindVoid;
        if (!t.IsValueType)
            return ReturnKindRef;
        if (fullName == "System.IntPtr" || fullName == "System.UIntPtr")
            return ReturnKindPointer;
        if (fullName == "System.Boolean")
            return ReturnKindBool;
        if (fullName == "System.Single" || fullName == "System.Double")
            return ReturnKindScalarFloat;        if (_scalarIntPrimitives.Contains(fullName))
            return ReturnKindScalarInt;
        try { if (t.BaseType?.FullName == "System.Enum") return ReturnKindScalarInt; } catch { }        return ReturnKindStruct;
    }

    private string[] GetVtableNames(TypeAnalysisContext type)
    {
        EnsureVtable(type);
        return _vtableNames[type];
    }

    private void EnsureVtable(TypeAnalysisContext type)
    {
        if (_vtableNames.ContainsKey(type))
            return;
        string[] names;
        TypeAnalysisContext[] returns;
        byte[] kinds;
        sbyte[] paramCounts;
        TypeAnalysisContext[][] paramTypes;
        try
        {
            MetadataUsage[] vtable = type.Definition.VTable;
            names = new string[vtable.Length];
            returns = new TypeAnalysisContext[vtable.Length];
            kinds = new byte[vtable.Length];
            paramCounts = new sbyte[vtable.Length];
            paramTypes = new TypeAnalysisContext[vtable.Length][];
            System.Array.Fill(paramCounts, (sbyte)-1);
            for (int i = 0; i < vtable.Length; i++)
            {
                MetadataUsage usage = vtable[i];
                if (usage == null)
                    continue;
                try
                {
                    if (usage.Type == MetadataUsageType.MethodDef)
                    {
                        Il2CppMethodDefinition method = usage.AsMethod();
                        if (method != null && method.slot == i)
                        {
                            names[i] = method.GlobalKey;
                            paramCounts[i] = method.parameterCount <= sbyte.MaxValue ? (sbyte)method.parameterCount : (sbyte)-1;
                            try
                            {
                                Il2CppType[] rawParams = method.InternalParameterTypes;
                                if (rawParams != null && rawParams.Length > 0)
                                {
                                    int take = rawParams.Length < 4 ? rawParams.Length : 4;
                                    TypeAnalysisContext[] resolvedParams = new TypeAnalysisContext[take];
                                    for (int p = 0; p < take; p++)
                                        resolvedParams[p] = rawParams[p] != null ? _app.ResolveIl2CppType(rawParams[p]) : null;
                                    paramTypes[i] = resolvedParams;
                                }
                            }
                            catch { }
                            if (method.RawReturnType != null)
                            {
                                TypeAnalysisContext resolved = _app.ResolveIl2CppType(method.RawReturnType);
                                kinds[i] = ClassifyReturn(resolved);
                                if (resolved != null && !resolved.IsValueType)
                                    returns[i] = resolved;                            }
                        }
                    }
                    else if (usage.Type == MetadataUsageType.MethodRef)
                    {
                        names[i] = usage.AsGenericMethodRef()?.ToString();                    }
                }
                catch { }
            }
        }
        catch { names = System.Array.Empty<string>(); returns = System.Array.Empty<TypeAnalysisContext>(); kinds = System.Array.Empty<byte>(); paramCounts = System.Array.Empty<sbyte>(); paramTypes = System.Array.Empty<TypeAnalysisContext[]>(); }
        _vtableNames[type] = names;
        _vtableReturns[type] = returns;
        _vtableReturnKinds[type] = kinds;
        _vtableParamCounts[type] = paramCounts;
        _vtableParamTypes[type] = paramTypes;
    }

    public TypeAnalysisContext GetVirtualParamType(TypeAnalysisContext type, int byteOffset, int paramIndex)
    {
        if (VtableOffset < 0 || type?.Definition == null || paramIndex < 0)
            return null;
        int slot = VtableSlotFromOffset(byteOffset);
        if (slot < 0)
            return null;
        EnsureVtable(type);
        TypeAnalysisContext[][] all = _vtableParamTypes[type];
        if (slot >= all.Length || all[slot] == null || paramIndex >= all[slot].Length)
            return null;
        return all[slot][paramIndex];
    }

    public int GetVirtualParamCount(TypeAnalysisContext type, int byteOffset)
    {
        if (VtableOffset < 0 || type?.Definition == null)
            return -1;
        int slot = VtableSlotFromOffset(byteOffset);
        if (slot < 0)
            return -1;
        EnsureVtable(type);
        sbyte[] counts = _vtableParamCounts[type];
        return slot < counts.Length ? counts[slot] : -1;
    }

    private int DiscoverVtableOffset(ApplicationAnalysisContext app)
    {
        if (app.Binary is not PE || app.Binary.is32Bit)
            return -1;

        Dictionary<int, int> votes = new();
        int scanned = 0;
        int candidates = 0;
        foreach (AssemblyAnalysisContext assembly in app.Assemblies)
        {
            foreach (TypeAnalysisContext type in assembly.Types)
            {
                foreach (MethodAnalysisContext method in type.Methods)
                {
                    if (method.UnderlyingPointer == 0 || method.IsStatic || type.Definition == null)
                        continue;
                    int vtableCount;
                    try { vtableCount = type.Definition.VTable?.Length ?? 0; }
                    catch { continue; }
                    if (vtableCount == 0)
                        continue;
                    if (scanned >= 5000 || candidates >= 1200)
                        goto done;
                    scanned++;
                    int thisReg = IsReturnedViaHiddenPointer(method.ReturnType) ? 2 : 1;                    ScanMethodForVtable(method, thisReg, vtableCount, votes, ref candidates);
                }
            }
        }

    done:
        int best = -1;
        int bestVotes = 0;
        foreach (KeyValuePair<int, int> vote in votes)
        {
            if (vote.Value > bestVotes)
            {
                bestVotes = vote.Value;
                best = vote.Key;
            }
        }
        return bestVotes >= 4 ? best : -1;
    }

    private void ScanMethodForVtable(MethodAnalysisContext method, int thisReg, int vtableCount, Dictionary<int, int> votes, ref int candidates)
    {
        byte[] bytes;
        try { method.EnsureRawBytes(); bytes = method.RawBytes.ToArray(); }
        catch { return; }
        if (bytes.Length == 0 || bytes.Length > 0x4000)
            return;

        Decoder decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), method.UnderlyingPointer);
        ulong end = method.UnderlyingPointer + (ulong)bytes.Length;
        int klassReg = -1;        int guard = 0;
        while (decoder.IP < end && guard++ < 8000)
        {
            decoder.Decode(out Instruction insn);
            if (insn.IsInvalid)
                break;

            if (klassReg >= 0 && insn.MemoryIndex == Register.None && RegisterFlowUtil.GpIndex(insn.MemoryBase) == klassReg
                && ((insn.Mnemonic == Mnemonic.Call && insn.Op0Kind == OpKind.Memory)
                    || (insn.Mnemonic == Mnemonic.Mov && insn.Op1Kind == OpKind.Memory)))
            {
                VoteVtableOffset((int)insn.MemoryDisplacement64, vtableCount, votes);
                candidates++;
            }

            if (insn.Mnemonic == Mnemonic.Mov && insn.Op0Kind == OpKind.Register && insn.Op1Kind == OpKind.Memory
                && insn.MemoryIndex == Register.None && RegisterFlowUtil.GpIndex(insn.MemoryBase) == thisReg
                && insn.MemoryDisplacement64 == 0)
            {
                klassReg = RegisterFlowUtil.GpIndex(insn.Op0Register);            }
            else if (klassReg >= 0 && insn.Op0Kind == OpKind.Register && RegisterFlowUtil.GpIndex(insn.Op0Register) == klassReg)
            {
                klassReg = -1;            }
        }
    }

    private static void VoteVtableOffset(int byteOffset, int vtableCount, Dictionary<int, int> votes)
    {
        for (int candidate = 0xF0; candidate <= 0x158; candidate += 8)
        {
            int offsetInVtable = byteOffset - candidate;
            if (offsetInVtable < 0)
                continue;
            if (offsetInVtable % 0x10 != 0 && offsetInVtable % 8 == 0)
                offsetInVtable -= 8;
            if (offsetInVtable < 0 || offsetInVtable % 0x10 != 0)
                continue;
            if (offsetInVtable / 0x10 < vtableCount)
                votes[candidate] = votes.TryGetValue(candidate, out int v) ? v + 1 : 1;
        }
    }
}
