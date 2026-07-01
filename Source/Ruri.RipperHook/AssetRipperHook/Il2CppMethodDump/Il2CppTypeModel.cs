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

/// <summary>
/// Per-application, cached layout model that turns raw memory offsets back into managed field symbols.
/// IL2CPP metadata already carries every field's byte offset (<see cref="FieldAnalysisContext.Offset"/>,
/// resolved via the binary field-offset table) and every runtime <c>Il2CppClass*</c> global
/// (<see cref="MetadataUsageType.TypeInfo"/>), so a register whose pointed-to type is known makes
/// <c>[reg+disp]</c> a resolvable field access — instance (<see cref="TryGetInstanceField"/>) or, via the
/// class's static-fields block (<see cref="StaticFieldsOffset"/>), static (<see cref="TryGetStaticField"/>).
/// The static-fields offset within <c>Il2CppClass</c> is version-specific, so it is discovered empirically
/// from the binary (see <see cref="DiscoverStaticFieldsOffset"/>) rather than hard-coded.
/// Built once per <see cref="ApplicationAnalysisContext"/>; the caller (<see cref="Il2CppAsmLookup"/>) holds a lock.
/// </summary>
internal sealed class Il2CppTypeModel
{
    private static Il2CppTypeModel _cached;
    private static ApplicationAnalysisContext _cachedApp;

    private readonly Dictionary<Il2CppTypeDefinition, TypeAnalysisContext> _byDefinition = new();
    private readonly Dictionary<TypeAnalysisContext, Dictionary<int, FieldAnalysisContext>> _instanceFields = new();
    private readonly Dictionary<TypeAnalysisContext, Dictionary<int, FieldAnalysisContext>> _staticFields = new();

    private readonly Dictionary<TypeAnalysisContext, string[]> _vtableNames = new(); // type → per-slot virtual method name
    private readonly Dictionary<TypeAnalysisContext, TypeAnalysisContext[]> _vtableReturns = new(); // type → per-slot virtual method return type (reference returns only)
    private readonly Dictionary<TypeAnalysisContext, byte[]> _vtableReturnKinds = new(); // type → per-slot return kind (see ReturnKind*)

    // Return-value kind of a named vtable slot — lets a caller pick a contradiction test that can never fire on a
    // correctly-named method. Void/Scalar leave nothing (void) or a non-pointer value (bool/int/float/enum) in rax, so
    // dereferencing rax is impossible; Struct returns a hidden buffer pointer in rax (deref is legitimate); Pointer
    // (IntPtr) is itself a pointer (deref legitimate); Ref is an object pointer (its slot 0 is the Il2CppClass*).
    public const byte ReturnKindUnresolved = 0;
    public const byte ReturnKindVoid = 1;
    public const byte ReturnKindScalar = 2;
    public const byte ReturnKindStruct = 3;
    public const byte ReturnKindRef = 4;
    public const byte ReturnKindPointer = 5;

    private static readonly HashSet<string> _scalarPrimitives = new()
    {
        "System.Boolean", "System.Byte", "System.SByte", "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.Char",
        "System.Single", "System.Double",
    };

    /// <summary>offsetof(Il2CppClass, static_fields) for this binary, or -1 if it could not be discovered.</summary>
    public int StaticFieldsOffset { get; private set; } = -1;

    /// <summary>offsetof(Il2CppClass, vtable) for this binary, or -1 if it could not be discovered.</summary>
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

    /// <summary>Maps a runtime <c>Il2CppClass*</c> / <c>Il2CppType*</c> metadata-usage global (VA) to its managed type.</summary>
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

    /// <summary>
    /// True if a method returning <paramref name="returnType"/> uses the hidden return-buffer-pointer ABI (first
    /// integer arg = pointer to caller-allocated result), which shifts <c>this</c> and every argument by one register.
    /// x64 rule: a value type is returned in a register only when its size is 1/2/4/8 bytes; otherwise via hidden pointer.
    /// </summary>
    public bool IsReturnedViaHiddenPointer(TypeAnalysisContext returnType)
    {
        if (returnType == null || !returnType.IsValueType || returnType.IsEnumType)
            return false;
        int size = EstimateValueTypeSize(returnType, 0);
        return size is not (1 or 2 or 4 or 8);
    }

    private int EstimateValueTypeSize(TypeAnalysisContext type, int depth)
    {
        if (type == null || !type.IsValueType || type.Definition == null || depth > 6)
            return 8;
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
                return EstimateValueTypeSize(type, depth); // nested struct
            default:
                return 8; // pointer-sized (ref types, IntPtr, enums fall back here)
        }
    }

    /// <summary>Instance field whose byte offset from the object pointer equals <paramref name="offset"/> (walks base types).</summary>
    public bool TryGetInstanceField(TypeAnalysisContext type, int offset, out FieldAnalysisContext field)
        => GetOffsetMap(_instanceFields, type, statics: false).TryGetValue(offset, out field);

    /// <summary>Static field whose byte offset within the type's static-fields block equals <paramref name="offset"/>.</summary>
    public bool TryGetStaticField(TypeAnalysisContext type, int offset, out FieldAnalysisContext field)
        => GetOffsetMap(_staticFields, type, statics: true).TryGetValue(offset, out field);

    // A generic REFERENCE instance (List<Foo>) has no Definition/fields of its own; its field layout equals the generic
    // definition's (List<T>) — every field is a pointer or fixed primitive, T only appears behind a reference (T[]), so
    // offsets are type-argument-independent. Only unwrap reference generics: a value-type generic (KeyValuePair<K,V>,
    // Nullable<T>) inlines its type-arg fields, shifting offsets, so the definition's offsets would be WRONG for a boxed
    // instance — leave those unresolved rather than mislabel.
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
            // Static storage is per-type (not inherited into the derived type's static-fields block).
            AddFields(map, type, statics: true);
        }
        else
        {
            // Instance layout is contiguous across the inheritance chain; walk derived -> base (unwrapping generics).
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
        bool allowZeroOffset = statics || type.IsValueType; // 0 is a valid field only in a struct / static block; on a ref type it is the Il2CppClass* header slot.
        foreach (FieldAnalysisContext field in type.Fields)
        {
            if (field.IsStatic != statics)
                continue;
            int offset;
            try { offset = field.Offset; }
            catch { continue; }
            if (offset < 0 || (offset == 0 && !allowZeroOffset))
                continue;
            map.TryAdd(offset, field); // derived is visited before base, so a derived field wins any (offset-impossible) collision
        }
    }

    /// <summary>
    /// Discovers offsetof(Il2CppClass, static_fields) by disassembling a bounded sample of methods and looking for the
    /// canonical static-field access idiom: <c>mov reg,[TypeInfo(T)] ; mov reg2,[reg + C] ; ... mov _,[reg2 + k]</c> where
    /// k matches a known static-field offset of T. The C that most often leads to a confirmed static read is the offset.
    /// Version-independent; returns -1 if nothing conclusive is found (static resolution then stays disabled).
    /// </summary>
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
        return bestCount >= 3 ? best : -1; // require a few independent confirmations to avoid noise
    }

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

        // Lightweight per-register facts, cleared on overwrite: which type a reg's Il2CppClass* points at,
        // and (for a reg loaded as [classReg + C]) the type + candidate C it may be a static-fields base of.
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

            // reg2 = [reg2Base + k] : confirm a prior candidate if k is a real static-field offset of its type.
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

            // Clear facts about the overwritten destination before recording new ones.
            typeInfoOf[dst] = null;
            staticBaseType[dst] = null;
            staticBaseCandidateC[dst] = -1;

            if (insn.IsIPRelativeMemoryOperand || (insn.MemoryBase == Register.None && insn.MemoryIndex == Register.None))
            {
                // reg = [global] : is it a TypeInfo/Type usage?
                if (TryGetTypeForTypeInfoGlobal(insn.MemoryDisplacement64, out TypeAnalysisContext infoType))
                    typeInfoOf[dst] = infoType;
            }
            else if (baseIndex >= 0 && insn.MemoryIndex == Register.None && typeInfoOf[baseIndex] != null)
            {
                // reg2 = [classReg + C] : C is a candidate static-fields offset for that class.
                staticBaseType[dst] = typeInfoOf[baseIndex];
                staticBaseCandidateC[dst] = (int)insn.MemoryDisplacement64;
            }
        }
    }

    /// <summary>
    /// Resolves a virtual/interface dispatch <c>[klass + byteOffset]</c> to the concrete method for <paramref name="type"/>.
    /// Uses the type's own metadata vtable (<see cref="Il2CppTypeDefinition.VTable"/>) — more accurate than Cpp2IL legacy's
    /// global slot map — with the empirically discovered <see cref="VtableOffset"/> and the 0x10-byte VirtualInvokeData stride
    /// ({ methodPtr, MethodInfo* }; a read of the second pointer is normalized by -8).
    /// </summary>
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
            offsetInVtable -= 8; // read of the MethodInfo* (second pointer of VirtualInvokeData)
        if (offsetInVtable < 0 || offsetInVtable % 0x10 != 0)
            return -1;
        return offsetInVtable / 0x10;
    }

    /// <summary>Return type of the virtual method dispatched by <c>[klass + byteOffset]</c> (for propagating <c>rax</c> after an indirect call), if a reference type.</summary>
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

    /// <summary>Return-value <c>ReturnKind*</c> of the virtual method dispatched by <c>[klass + byteOffset]</c>; <see cref="ReturnKindUnresolved"/> if unknown.</summary>
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
        if (_scalarPrimitives.Contains(fullName))
            return ReturnKindScalar;
        try { if (t.BaseType?.FullName == "System.Enum") return ReturnKindScalar; } catch { } // enums are register-returned like their underlying primitive
        return ReturnKindStruct;
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
        try
        {
            MetadataUsage[] vtable = type.Definition.VTable;
            names = new string[vtable.Length];
            returns = new TypeAnalysisContext[vtable.Length];
            kinds = new byte[vtable.Length];
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
                        // VALIDITY FILTER: only trust the entry when the method's own canonical slot equals this vtable
                        // index. A class virtual (own or inherited) always sits at its own slot; an INTERFACE-implementation
                        // slot places a method at an index != its slot, and the `call [klass+off]` there dispatches an
                        // interface method whose concrete impl depends on the runtime (derived) type — naming it after this
                        // decode mislabels it (proven: UI `Graphic` slot 21 aliases `IsDestroyed`, whose real slot is 16,
                        // for what is actually a `Color`-taking call). Suppress -> a miss, never a WRONG symbol.
                        if (method != null && method.slot == i)
                        {
                            names[i] = method.GlobalKey;
                            if (method.RawReturnType != null)
                            {
                                TypeAnalysisContext resolved = type.DeclaringAssembly.ResolveIl2CppType(method.RawReturnType);
                                kinds[i] = ClassifyReturn(resolved);
                                if (resolved != null && !resolved.IsValueType)
                                    returns[i] = resolved; // only reference returns are useful for chaining
                            }
                        }
                    }
                    else if (usage.Type == MetadataUsageType.MethodRef)
                    {
                        names[i] = usage.AsGenericMethodRef()?.ToString(); // generic instance impls are already index-specific
                    }
                }
                catch { }
            }
        }
        catch { names = System.Array.Empty<string>(); returns = System.Array.Empty<TypeAnalysisContext>(); kinds = System.Array.Empty<byte>(); }
        _vtableNames[type] = names;
        _vtableReturns[type] = returns;
        _vtableReturnKinds[type] = kinds;
    }

    /// <summary>
    /// Discovers offsetof(Il2CppClass, vtable) by disassembling a bounded sample of instance methods and looking for the
    /// virtual-dispatch idiom: <c>mov klass,[this] ; ... (call|mov) [klass + N]</c>. The vtable offset is the candidate C
    /// for which <c>(N - C)/0x10</c> most often lands on a real vtable slot of the calling type. Version-independent; -1 if inconclusive.
    /// </summary>
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
                    int thisReg = IsReturnedViaHiddenPointer(method.ReturnType) ? 2 : 1; // rdx if hidden-return buffer takes rcx, else rcx
                    ScanMethodForVtable(method, thisReg, vtableCount, votes, ref candidates);
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
        int klassReg = -1; // GP index currently holding Klass(this)
        int guard = 0;
        while (decoder.IP < end && guard++ < 8000)
        {
            decoder.Decode(out Instruction insn);
            if (insn.IsInvalid)
                break;

            // (call|mov) through [klassReg + N] -> a dispatch through the class; vote every plausible vtable offset.
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
                klassReg = RegisterFlowUtil.GpIndex(insn.Op0Register); // klass = [this]
            }
            else if (klassReg >= 0 && insn.Op0Kind == OpKind.Register && RegisterFlowUtil.GpIndex(insn.Op0Register) == klassReg)
            {
                klassReg = -1; // klass register overwritten
            }
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
