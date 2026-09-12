using CUE4Parse.MappingsProvider.Usmap;

namespace Ruri.Tpk.Unreal.Reflection;

/// <summary>
/// The engine's own construction of its reflection, replayed over the static data the compiler
/// laid out. Every Z_Construct_* statics block is walked the way UObjectGlobals.cpp walks it --
/// ConstructUClass, ConstructUScriptStruct, ConstructUEnum and the backward ConstructFProperty
/// walk that hands a container its inner properties -- so names, order and shapes come out as
/// the running game registers them. Class names follow IMPLEMENT_CLASS (the C++ name past its
/// prefix, past DEPRECATED_ when the class is), enumerations follow UEnum::SetEnums (a _MAX
/// entry added when none exists) and UEnum::GetNameStringByIndex (the scope stripped for
/// namespaced and class enums). Layouts and enumerator values come from the program database.
/// </summary>
internal sealed class CodeGenReader
{
    private const string CodeGenNamespace = "UECodeGen_Private::";
    private const string ClassConstructor = "Z_Construct_UClass_";
    private const string StructConstructor = "Z_Construct_UScriptStruct_";
    private const string EnumConstructor = "Z_Construct_UEnum_";
    private const string AnyConstructor = "Z_Construct_U";
    private const string StaticsSuffix = "_Statics";
    private const string NoRegisterSuffix = "_NoRegister";
    private const string ClassParamsMember = "::ClassParams";
    private const string StructParamsMember = "::StructParams";
    private const string EnumParamsMember = "::EnumParams";
    private const string DeprecatedClassPrefix = "DEPRECATED_";
    private const string ScopeSeparator = "::";
    private const string MaxSuffix = "_MAX";
    private const string MaxName = "MAX";

    /// <summary>UObjectGlobals.h: <c>inline constexpr EPropertyGenFlags PropertyTypeMask = (EPropertyGenFlags)0x3F;</c> -- a constexpr, so it has no record to read.</summary>
    private const byte PropertyTypeMask = 0x3F;

    private static readonly string[] LayoutNames =
    [
        CodeGenNamespace + "FClassParams",
        CodeGenNamespace + "FStructParams",
        CodeGenNamespace + "FEnumParams",
        CodeGenNamespace + "FEnumeratorParam",
        CodeGenNamespace + "FPropertyParamsBase",
        CodeGenNamespace + "FBytePropertyParams",
        CodeGenNamespace + "FEnumPropertyParams",
        CodeGenNamespace + "FStructPropertyParams",
    ];

    private static readonly string[] EnumNames =
    [
        CodeGenNamespace + "EPropertyGenFlags",
        "EClassFlags",
        "EEnumFlags",
        "UEnum::ECppForm",
    ];

    private readonly ProgramImage image;
    private readonly ProgramSymbols symbols;
    private readonly Dictionary<string, string> structNameByConstructor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> enumNameByConstructor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> classNameByConstructor = new(StringComparer.Ordinal);
    private readonly Dictionary<byte, string> propertyKinds = new();

    private CodeGenLayout classParams = null!;
    private CodeGenLayout structParams = null!;
    private CodeGenLayout enumParams = null!;
    private CodeGenLayout enumeratorParam = null!;
    private CodeGenLayout propertyBase = null!;
    private CodeGenLayout byteProperty = null!;
    private CodeGenLayout enumProperty = null!;
    private CodeGenLayout structProperty = null!;
    private long deprecatedClassFlag;
    private long flagsEnumFlag;
    private long regularCppForm;

    public CodeGenReader(ProgramImage image, ProgramSymbols symbols)
    {
        this.image = image;
        this.symbols = symbols;
    }

    public ReflectedSchema Read()
    {
        List<(string Constructor, uint Rva)> classes = new();
        List<(string Constructor, uint Rva)> structs = new();
        List<(string Constructor, uint Rva)> enums = new();
        HashSet<string> classConstructors = new(StringComparer.Ordinal);
        foreach (string name in symbols.Names)
        {
            if (TryStatics(name, ClassConstructor, ClassParamsMember, out string constructor))
            {
                classes.Add((constructor, symbols.Rva(name)));
            }
            else if (TryStatics(name, StructConstructor, StructParamsMember, out constructor))
            {
                structs.Add((constructor, symbols.Rva(name)));
            }
            else if (TryStatics(name, EnumConstructor, EnumParamsMember, out constructor))
            {
                enums.Add((constructor, symbols.Rva(name)));
            }
            else if (IsClassConstructor(name))
            {
                classConstructors.Add(name);
            }
        }
        foreach ((string constructor, _) in classes)
        {
            classConstructors.Remove(constructor);
        }
        List<string> intrinsic = classConstructors.ToList();
        Dictionary<string, string> intrinsicSupers = ReadTypes(intrinsic.ConvertAll(static constructor => constructor[ClassConstructor.Length..]));

        foreach ((string constructor, uint rva) in structs)
        {
            structNameByConstructor[constructor] = image.ReadUtf8(image.ReadPointer(rva + structParams.Offset("NameUTF8")));
        }
        foreach ((string constructor, uint rva) in enums)
        {
            enumNameByConstructor[constructor] = image.ReadUtf8(image.ReadPointer(rva + enumParams.Offset("NameUTF8")));
        }
        foreach ((string constructor, uint rva) in classes)
        {
            classNameByConstructor[constructor] = ClassName(constructor, rva);
        }
        foreach (string constructor in intrinsic)
        {
            classNameByConstructor[constructor] = constructor[(ClassConstructor.Length + 1)..];
        }

        ReflectedSchema schema = new();
        foreach ((string constructor, uint rva) in enums)
        {
            schema.Enums.Add(ReadEnum(constructor, rva));
        }
        foreach ((string constructor, uint rva) in structs)
        {
            schema.Structs.Add(ReadStruct(constructor, rva));
        }
        foreach ((string constructor, uint rva) in classes)
        {
            schema.Structs.Add(ReadClass(constructor, rva));
        }
        foreach (string constructor in intrinsic)
        {
            if (intrinsicSupers.TryGetValue(constructor[ClassConstructor.Length..], out string? superCppName))
            {
                schema.Structs.Add(ReadIntrinsicClass(constructor, superCppName));
            }
            else
            {
                schema.OmittedClasses.Add(classNameByConstructor[constructor]);
            }
        }
        schema.OmittedClasses.Sort(StringComparer.Ordinal);
        schema.Enums.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        schema.Structs.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return schema;
    }

    private static bool IsClassConstructor(string symbol) =>
        symbol.StartsWith(ClassConstructor, StringComparison.Ordinal)
        && !symbol.EndsWith(NoRegisterSuffix, StringComparison.Ordinal)
        && !symbol.Contains(ScopeSeparator, StringComparison.Ordinal);

    /// <summary>
    /// A class the engine declares intrinsically (UObject, UInterface, the reflection types
    /// themselves) has a constructor but no statics: no properties, and a parent stated only by
    /// DECLARE_CLASS -- which the program database keeps as the class's Super typedef. A class
    /// whose parent is itself is the root.
    /// </summary>
    private ReflectedStruct ReadIntrinsicClass(string constructor, string superCppName)
    {
        string cppName = constructor[ClassConstructor.Length..];
        string? super = null;
        if (superCppName != cppName)
        {
            super = classNameByConstructor.TryGetValue(ClassConstructor + superCppName, out string? superName)
                ? superName
                : throw new InvalidDataException($"Parent '{superCppName}' of intrinsic class {cppName} has no constructor symbol.");
        }
        return new ReflectedStruct(classNameByConstructor[constructor], super, Array.Empty<ReflectedProperty>());
    }

    private Dictionary<string, string> ReadTypes(IReadOnlyCollection<string> intrinsicClasses)
    {
        (Dictionary<string, CodeGenLayout> layouts, Dictionary<string, Dictionary<string, long>> enums, Dictionary<string, string> supers) =
            symbols.ReadTypes(LayoutNames, EnumNames, intrinsicClasses);
        classParams = layouts[CodeGenNamespace + "FClassParams"];
        structParams = layouts[CodeGenNamespace + "FStructParams"];
        enumParams = layouts[CodeGenNamespace + "FEnumParams"];
        enumeratorParam = layouts[CodeGenNamespace + "FEnumeratorParam"];
        propertyBase = layouts[CodeGenNamespace + "FPropertyParamsBase"];
        byteProperty = layouts[CodeGenNamespace + "FBytePropertyParams"];
        enumProperty = layouts[CodeGenNamespace + "FEnumPropertyParams"];
        structProperty = layouts[CodeGenNamespace + "FStructPropertyParams"];
        deprecatedClassFlag = enums["EClassFlags"]["CLASS_Deprecated"];
        flagsEnumFlag = enums["EEnumFlags"]["Flags"];
        regularCppForm = enums["UEnum::ECppForm"]["Regular"];
        foreach ((string member, long value) in enums[CodeGenNamespace + "EPropertyGenFlags"])
        {
            if (member != "None" && value >= 0 && value <= PropertyTypeMask)
            {
                propertyKinds.TryAdd((byte)value, member);
            }
        }
        return supers;
    }

    private static bool TryStatics(string symbol, string constructorPrefix, string member, out string constructor)
    {
        constructor = string.Empty;
        if (!symbol.StartsWith(constructorPrefix, StringComparison.Ordinal) || !symbol.EndsWith(member, StringComparison.Ordinal))
        {
            return false;
        }
        string scope = symbol[..^member.Length];
        if (!scope.EndsWith(StaticsSuffix, StringComparison.Ordinal))
        {
            return false;
        }
        constructor = scope[..^StaticsSuffix.Length];
        return true;
    }

    private string ClassName(string constructor, uint rva)
    {
        string cppName = constructor[ClassConstructor.Length..];
        uint flags = image.ReadUInt32(rva + classParams.Offset("ClassFlags"));
        int skip = 1 + ((flags & deprecatedClassFlag) != 0 ? DeprecatedClassPrefix.Length : 0);
        return cppName[skip..];
    }

    private ReflectedEnum ReadEnum(string constructor, uint rva)
    {
        string name = enumNameByConstructor[constructor];
        short count = image.ReadInt16(rva + enumParams.Offset("NumEnumerators"));
        bool regular = image.ReadByte(rva + enumParams.Offset("CppForm")) == regularCppForm;
        bool flags = (image.ReadByte(rva + enumParams.Offset("EnumFlags")) & flagsEnumFlag) != 0;
        List<ReflectedEnumerator> names = new(count + 1);
        if (count > 0)
        {
            uint first = image.RvaOf(image.ReadPointer(rva + enumParams.Offset("EnumeratorParams")));
            for (uint index = 0; index < (uint)count; index++)
            {
                uint entry = first + index * enumeratorParam.Size;
                names.Add(new ReflectedEnumerator(
                    image.ReadUtf8(image.ReadPointer(entry + enumeratorParam.Offset("NameUTF8"))),
                    image.ReadInt64(entry + enumeratorParam.Offset("Value"))));
            }
        }
        AddMaxIfMissing(name, names, regular, flags);
        ReflectedEnumerator[] entries = new ReflectedEnumerator[names.Count];
        for (int index = 0; index < names.Count; index++)
        {
            entries[index] = new ReflectedEnumerator(DisplayName(names[index].Name, regular), names[index].Value);
        }
        return new ReflectedEnum(name, entries);
    }

    private static void AddMaxIfMissing(string enumName, List<ReflectedEnumerator> names, bool regular, bool flags)
    {
        if (ContainsExistingMax(enumName, names, regular))
        {
            return;
        }
        string maxName = FullEnumName(enumName, GenerateEnumPrefix(enumName, names) + MaxSuffix, regular);
        names.Add(new ReflectedEnumerator(maxName, MaxEnumValue(names, flags) + 1));
    }

    private static bool ContainsExistingMax(string enumName, List<ReflectedEnumerator> names, bool regular)
    {
        string max = FullEnumName(enumName, MaxName, regular);
        string prefixedMax = FullEnumName(enumName, GenerateEnumPrefix(enumName, names) + MaxSuffix, regular);
        foreach (ReflectedEnumerator entry in names)
        {
            if (entry.Name == max || entry.Name == prefixedMax)
            {
                return true;
            }
        }
        return false;
    }

    private static string FullEnumName(string enumName, string entry, bool regular) =>
        regular || entry.Contains(ScopeSeparator, StringComparison.Ordinal) ? entry : enumName + ScopeSeparator + entry;

    private static string GenerateEnumPrefix(string enumName, List<ReflectedEnumerator> names)
    {
        string prefix = string.Empty;
        if (names.Count > 0)
        {
            prefix = names[0].Name;
            for (int index = 1; index < names.Count; index++)
            {
                string item = names[index].Name;
                int common = 0;
                while (common < prefix.Length && common < item.Length && prefix[common] == item[common])
                {
                    common++;
                }
                prefix = prefix[..common];
            }
            int underscore = prefix.LastIndexOf('_');
            prefix = underscore > 0 ? prefix[..underscore] : string.Empty;
        }
        return prefix.Length == 0 ? enumName : prefix;
    }

    private static long MaxEnumValue(List<ReflectedEnumerator> names, bool flags)
    {
        if (names.Count == 0)
        {
            return 0;
        }
        if (flags)
        {
            long maxFlag = 0;
            foreach (ReflectedEnumerator entry in names)
            {
                if ((entry.Value & (entry.Value - 1)) == 0)
                {
                    maxFlag |= entry.Value;
                }
            }
            return maxFlag;
        }
        long max = names[0].Value;
        for (int index = 1; index < names.Count; index++)
        {
            if (names[index].Value > max)
            {
                max = names[index].Value;
            }
        }
        return max;
    }

    private static string DisplayName(string fullName, bool regular)
    {
        if (regular)
        {
            return fullName;
        }
        int scope = fullName.IndexOf(ScopeSeparator, StringComparison.Ordinal);
        return scope >= 0 ? fullName[(scope + ScopeSeparator.Length)..] : string.Empty;
    }

    private ReflectedStruct ReadStruct(string constructor, uint rva)
    {
        ulong superFunction = image.ReadPointer(rva + structParams.Offset("SuperFunc"));
        string? super = superFunction == 0 ? null : StructName(superFunction);
        ulong array = image.ReadPointer(rva + structParams.Offset("PropertyArray"));
        int count = image.ReadUInt16(rva + structParams.Offset("NumProperties"));
        return new ReflectedStruct(structNameByConstructor[constructor], super, ReadProperties(array, count));
    }

    private ReflectedStruct ReadClass(string constructor, uint rva)
    {
        string? super = null;
        uint dependencyCount = ReadBits(rva, classParams.Get("NumDependencySingletons"));
        if (dependencyCount > 0)
        {
            ulong first = image.ReadPointer(image.RvaOf(image.ReadPointer(rva + classParams.Offset("DependencySingletonFuncArray"))));
            string dependency = symbols.FunctionAt(first, ClassConstructor, AnyConstructor);
            if (dependency.StartsWith(ClassConstructor, StringComparison.Ordinal))
            {
                super = classNameByConstructor.TryGetValue(dependency, out string? superName)
                    ? superName
                    : throw new InvalidDataException($"Super class '{dependency}' of {constructor} has no statics block.");
            }
        }
        ulong array = image.ReadPointer(rva + classParams.Offset("PropertyArray"));
        int count = (int)ReadBits(rva, classParams.Get("NumProperties"));
        return new ReflectedStruct(classNameByConstructor[constructor], super, ReadProperties(array, count));
    }

    private uint ReadBits(uint rva, CodeGenLayout.Member member)
    {
        uint word = image.ReadUInt32(rva + member.Offset);
        return member.IsBitField ? (word >> member.BitPosition) & ((1u << member.BitLength) - 1) : word;
    }

    private IReadOnlyList<ReflectedProperty> ReadProperties(ulong array, int count)
    {
        if (count == 0)
        {
            return Array.Empty<ReflectedProperty>();
        }
        uint arrayRva = image.RvaOf(array);
        List<ReflectedProperty> constructed = new(count);
        int index = count;
        while (index > 0)
        {
            constructed.Add(ReadProperty(arrayRva, ref index));
        }
        constructed.Reverse();
        return constructed;
    }

    private ReflectedProperty ReadProperty(uint arrayRva, ref int index)
    {
        if (index <= 0)
        {
            throw new InvalidDataException("A container property asks for an inner property the array does not hold.");
        }
        uint parameters = image.RvaOf(image.ReadPointer(arrayRva + (uint)(--index) * ProgramImage.PointerSize));
        string name = image.ReadUtf8(image.ReadPointer(parameters + propertyBase.Offset("NameUTF8")));
        byte kind = (byte)(image.ReadByte(parameters + propertyBase.Offset("Flags")) & PropertyTypeMask);
        int arrayDim = image.ReadUInt16(parameters + propertyBase.Offset("ArrayDim"));
        return new ReflectedProperty(name, arrayDim, TypeOf(kind, parameters, arrayRva, ref index));
    }

    private ReflectedType TypeOf(byte kind, uint parameters, uint arrayRva, ref int index)
    {
        if (!propertyKinds.TryGetValue(kind, out string? member))
        {
            throw new InvalidDataException($"Property kind 0x{kind:X} is not a member of EPropertyGenFlags in this build.");
        }
        switch (member)
        {
            case "Byte":
                ulong byteEnum = image.ReadPointer(parameters + byteProperty.Offset("EnumFunc"));
                return byteEnum == 0
                    ? new ReflectedType(EPropertyType.ByteProperty)
                    : new ReflectedType(EPropertyType.EnumProperty, EnumName: EnumName(byteEnum), Inner: new ReflectedType(EPropertyType.ByteProperty));
            case "Int8":
                return new ReflectedType(EPropertyType.Int8Property);
            case "Int16":
                return new ReflectedType(EPropertyType.Int16Property);
            case "Int":
                return new ReflectedType(EPropertyType.IntProperty);
            case "Int64":
                return new ReflectedType(EPropertyType.Int64Property);
            case "UInt16":
                return new ReflectedType(EPropertyType.UInt16Property);
            case "UInt32":
                return new ReflectedType(EPropertyType.UInt32Property);
            case "UInt64":
                return new ReflectedType(EPropertyType.UInt64Property);
            case "Float":
                return new ReflectedType(EPropertyType.FloatProperty);
            case "Double":
            case "LargeWorldCoordinatesReal":
                return new ReflectedType(EPropertyType.DoubleProperty);
            case "Bool":
                return new ReflectedType(EPropertyType.BoolProperty);
            case "SoftClass":
                return new ReflectedType(EPropertyType.SoftClassProperty);
            case "WeakObject":
                return new ReflectedType(EPropertyType.WeakObjectProperty);
            case "LazyObject":
                return new ReflectedType(EPropertyType.LazyObjectProperty);
            case "SoftObject":
                return new ReflectedType(EPropertyType.SoftObjectProperty);
            case "Class":
                return new ReflectedType(EPropertyType.ClassProperty);
            case "Object":
                return new ReflectedType(EPropertyType.ObjectProperty);
            case "Interface":
                return new ReflectedType(EPropertyType.InterfaceProperty);
            case "Name":
                return new ReflectedType(EPropertyType.NameProperty);
            case "Str":
                return new ReflectedType(EPropertyType.StrProperty);
            case "Array":
                return new ReflectedType(EPropertyType.ArrayProperty, Inner: ReadInner(arrayRva, ref index));
            case "Map":
                ReflectedType key = ReadInner(arrayRva, ref index);
                ReflectedType value = ReadInner(arrayRva, ref index);
                return new ReflectedType(EPropertyType.MapProperty, Inner: key, Value: value);
            case "Set":
                return new ReflectedType(EPropertyType.SetProperty, Inner: ReadInner(arrayRva, ref index));
            case "Struct":
                return new ReflectedType(EPropertyType.StructProperty, StructName: StructName(image.ReadPointer(parameters + structProperty.Offset("ScriptStructFunc"))));
            case "Delegate":
                return new ReflectedType(EPropertyType.DelegateProperty);
            case "InlineMulticastDelegate":
                return new ReflectedType(EPropertyType.MulticastInlineDelegateProperty);
            case "SparseMulticastDelegate":
                return new ReflectedType(EPropertyType.MulticastDelegateProperty);
            case "Text":
                return new ReflectedType(EPropertyType.TextProperty);
            case "Enum":
                string enumName = EnumName(image.ReadPointer(parameters + enumProperty.Offset("EnumFunc")));
                return new ReflectedType(EPropertyType.EnumProperty, EnumName: enumName, Inner: ReadInner(arrayRva, ref index));
            case "FieldPath":
                return new ReflectedType(EPropertyType.FieldPathProperty);
            case "Optional":
                return new ReflectedType(EPropertyType.OptionalProperty, Inner: ReadInner(arrayRva, ref index));
            case "Utf8Str":
                return new ReflectedType(EPropertyType.Utf8StrProperty);
            case "AnsiStr":
                return new ReflectedType(EPropertyType.AnsiStrProperty);
            case "VValue":
                return new ReflectedType(EPropertyType.Unknown);
            default:
                throw new InvalidDataException($"Property kind '{member}' has no usmap type.");
        }
    }

    private ReflectedType ReadInner(uint arrayRva, ref int index) => ReadProperty(arrayRva, ref index).Type;

    private string StructName(ulong constructorPointer)
    {
        string constructor = symbols.FunctionAt(constructorPointer, StructConstructor);
        return structNameByConstructor.TryGetValue(constructor, out string? name)
            ? name
            : throw new InvalidDataException($"Struct '{constructor}' has no statics block.");
    }

    private string EnumName(ulong constructorPointer)
    {
        string constructor = symbols.FunctionAt(constructorPointer, EnumConstructor);
        return enumNameByConstructor.TryGetValue(constructor, out string? name)
            ? name
            : throw new InvalidDataException($"Enumeration '{constructor}' has no statics block.");
    }
}
