using AsmResolver.Symbols.Pdb;
using AsmResolver.Symbols.Pdb.Leaves;
using AsmResolver.Symbols.Pdb.Records;

namespace Ruri.Tpk.Unreal.Reflection;

/// <summary>
/// The program database beside the executable: every public and global data symbol as a name
/// at an address (and every address as its names, since folded functions share one), plus the
/// layouts and enumerations the type stream records for the code-gen structures. Public names
/// arrive decorated; they are restated as the qualified C++ names the source spells.
/// </summary>
internal sealed class ProgramSymbols
{
    private const byte JumpRelative32 = 0xE9;

    private readonly PdbImage pdb;
    private readonly ProgramImage image;
    private readonly Dictionary<string, uint> rvaByName = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, List<string>> namesByRva = new();

    public ProgramSymbols(string pdbPath, ProgramImage image)
    {
        this.image = image;
        pdb = PdbImage.FromFile(pdbPath);
        foreach (ICodeViewSymbol symbol in pdb.Symbols)
        {
            switch (symbol)
            {
                case PublicSymbol publicSymbol when publicSymbol.Name is not null:
                    Register(Undecorate(publicSymbol.Name.Value), publicSymbol.SegmentIndex, publicSymbol.Offset);
                    break;
                case DataSymbol dataSymbol when dataSymbol.Name is not null:
                    Register(dataSymbol.Name.Value, dataSymbol.SegmentIndex, dataSymbol.Offset);
                    break;
            }
        }
    }

    public int Count => rvaByName.Count;

    public IEnumerable<string> Names => rvaByName.Keys;

    public uint Rva(string name) =>
        rvaByName.TryGetValue(name, out uint rva) ? rva : throw new KeyNotFoundException($"Symbol '{name}' is not in the program database.");

    public IReadOnlyList<string> NamesAt(uint rva) => namesByRva.TryGetValue(rva, out List<string>? names) ? names : Array.Empty<string>();

    /// <summary>
    /// The function a pointer in static data names, chosen by prefix among whatever symbols share
    /// its address, through one relative jump when incremental linking put a thunk in between.
    /// </summary>
    public string FunctionAt(ulong pointer, params string[] prefixes)
    {
        uint rva = image.RvaOf(pointer);
        string? name = Pick(rva, prefixes);
        if (name is null && image.ReadByte(rva) == JumpRelative32)
        {
            int relative = image.ReadInt32(rva + 1);
            name = Pick((uint)(rva + 5 + relative), prefixes);
        }
        return name ?? throw new InvalidDataException(
            $"No function named like [{string.Join(", ", prefixes)}] at RVA 0x{rva:X}; the symbols there are [{string.Join(", ", NamesAt(rva))}].");
    }

    /// <summary>
    /// One pass over the type stream for every layout, enumeration and class parent named,
    /// which is the whole reason the executable's own database is read: no offset, no
    /// enumerator value and no intrinsic class's parent is assumed. A parent comes from the
    /// <c>Super</c> typedef DECLARE_CLASS leaves inside every UObject class; a class the
    /// database never recorded is simply absent from the parents returned, for the caller to
    /// report. Layouts and enumerations, by contrast, are demanded: nothing can be read without them.
    /// </summary>
    public (Dictionary<string, CodeGenLayout> Layouts, Dictionary<string, Dictionary<string, long>> Enums, Dictionary<string, string> Supers) ReadTypes(
        IReadOnlyCollection<string> classNames, IReadOnlyCollection<string> enumNames, IReadOnlyCollection<string> parentOfClasses)
    {
        HashSet<string> wantedClasses = new(classNames, StringComparer.Ordinal);
        HashSet<string> wantedEnums = new(enumNames, StringComparer.Ordinal);
        HashSet<string> wantedSupers = new(parentOfClasses, StringComparer.Ordinal);
        Dictionary<string, CodeGenLayout> layouts = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, long>> enums = new(StringComparer.Ordinal);
        Dictionary<string, string> supers = new(StringComparer.Ordinal);
        foreach (ITpiLeaf leaf in pdb.GetLeafRecords())
        {
            switch (leaf)
            {
                case ClassTypeRecord record when record.Name is not null && record.Fields is not null:
                    string recordName = record.Name.Value;
                    if (record.Size > 0 && wantedClasses.Contains(recordName))
                    {
                        layouts.TryAdd(recordName, CodeGenLayout.From(record));
                    }
                    else if (wantedSupers.Contains(recordName) && !supers.ContainsKey(recordName) && SuperOf(record) is string super)
                    {
                        supers[recordName] = super;
                    }
                    break;
                case EnumTypeRecord record when record.Name is not null && record.Fields is not null && wantedEnums.Contains(record.Name.Value):
                    enums.TryAdd(record.Name.Value, EnumValues(record));
                    break;
            }
            if (layouts.Count == wantedClasses.Count && enums.Count == wantedEnums.Count && supers.Count == wantedSupers.Count)
            {
                break;
            }
        }
        foreach (string name in wantedClasses)
        {
            if (!layouts.ContainsKey(name))
            {
                throw new InvalidDataException($"Type '{name}' has no complete record in the program database.");
            }
        }
        foreach (string name in wantedEnums)
        {
            if (!enums.ContainsKey(name))
            {
                throw new InvalidDataException($"Enumeration '{name}' has no record in the program database.");
            }
        }
        return (layouts, enums, supers);
    }

    private static string? SuperOf(ClassTypeRecord record)
    {
        foreach (CodeViewField field in record.Fields!.Entries)
        {
            if (field is not NestedTypeField nested || nested.Name is null || nested.Name.Value != "Super")
            {
                continue;
            }
            CodeViewTypeRecord? type = nested.NestedType;
            while (type is ModifierTypeRecord modifier)
            {
                type = modifier.BaseType;
            }
            return type is ClassTypeRecord parent ? parent.Name?.Value : null;
        }
        return null;
    }

    /// <summary>
    /// The qualified name inside an MSVC decorated public symbol: the scopes between the leading
    /// question mark and the first double at-sign, innermost first, restated outermost first.
    /// A name that is not decorated is already the name.
    /// </summary>
    public static string Undecorate(string decorated)
    {
        if (decorated.Length < 2 || decorated[0] != '?')
        {
            return decorated;
        }
        int end = decorated.IndexOf("@@", 1, StringComparison.Ordinal);
        if (end < 0)
        {
            return decorated;
        }
        string[] scopes = decorated.Substring(1, end - 1).Split('@');
        Array.Reverse(scopes);
        return string.Join("::", scopes);
    }

    private void Register(string name, ushort segment, uint offset)
    {
        if (!image.TryRva(segment, offset, out uint rva))
        {
            return;
        }
        rvaByName.TryAdd(name, rva);
        if (!namesByRva.TryGetValue(rva, out List<string>? names))
        {
            names = new List<string>(1);
            namesByRva[rva] = names;
        }
        if (!names.Contains(name))
        {
            names.Add(name);
        }
    }

    private string? Pick(uint rva, string[] prefixes)
    {
        IReadOnlyList<string> names = NamesAt(rva);
        foreach (string prefix in prefixes)
        {
            foreach (string name in names)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return name;
                }
            }
        }
        return null;
    }

    private static Dictionary<string, long> EnumValues(EnumTypeRecord record)
    {
        Dictionary<string, long> values = new(StringComparer.Ordinal);
        foreach (CodeViewField field in record.Fields!.Entries)
        {
            if (field is EnumerateField enumerator && enumerator.Name is not null)
            {
                values[enumerator.Name.Value] = enumerator.Value is ulong unsignedValue
                    ? unchecked((long)unsignedValue)
                    : Convert.ToInt64(enumerator.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return values;
    }
}
