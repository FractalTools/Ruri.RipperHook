using System.Text;
using CUE4Parse.MappingsProvider.Usmap;

namespace Ruri.Tpk.Unreal.Reflection;

/// <summary>
/// A schema as a .usmap file, laid out exactly as CUE4Parse's UsmapParser reads it back at its
/// latest version: a name table with 16-bit lengths, enumerations with explicit 64-bit values,
/// then every struct with its super, its slot count (each static array element is a slot) and
/// its properties as slot index, array size, name and type. Stored uncompressed; the file is
/// small and the reader needs no codec for it.
/// </summary>
internal static class UsmapWriter
{
    private const ushort Magic = 0x30C4;
    private const int NoName = -1;

    public static void Write(ReflectedSchema schema, string path)
    {
        NameTable names = new();
        foreach (ReflectedEnum enumeration in schema.Enums)
        {
            names.Add(enumeration.Name);
            foreach (ReflectedEnumerator entry in enumeration.Entries)
            {
                names.Add(entry.Name);
            }
        }
        foreach (ReflectedStruct structure in schema.Structs)
        {
            names.Add(structure.Name);
            if (structure.Super is not null)
            {
                names.Add(structure.Super);
            }
            foreach (ReflectedProperty property in structure.Properties)
            {
                names.Add(property.Name);
                AddTypeNames(names, property.Type);
            }
        }

        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((uint)names.Count);
            foreach (string name in names.Names)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(name);
                if (bytes.Length > ushort.MaxValue)
                {
                    throw new InvalidDataException($"Name '{name[..32]}...' is longer than a 16-bit length can state.");
                }
                writer.Write((ushort)bytes.Length);
                writer.Write(bytes);
            }

            writer.Write((uint)schema.Enums.Count);
            foreach (ReflectedEnum enumeration in schema.Enums)
            {
                writer.Write(names[enumeration.Name]);
                if (enumeration.Entries.Count > ushort.MaxValue)
                {
                    throw new InvalidDataException($"Enumeration '{enumeration.Name}' has more entries than a 16-bit count can state.");
                }
                writer.Write((ushort)enumeration.Entries.Count);
                foreach (ReflectedEnumerator entry in enumeration.Entries)
                {
                    writer.Write(unchecked((ulong)entry.Value));
                    writer.Write(names[entry.Name]);
                }
            }

            writer.Write((uint)schema.Structs.Count);
            foreach (ReflectedStruct structure in schema.Structs)
            {
                writer.Write(names[structure.Name]);
                writer.Write(structure.Super is null ? NoName : names[structure.Super]);
                int slots = 0;
                foreach (ReflectedProperty property in structure.Properties)
                {
                    slots += property.ArrayDim;
                }
                if (slots > ushort.MaxValue || structure.Properties.Count > ushort.MaxValue)
                {
                    throw new InvalidDataException($"Struct '{structure.Name}' has more property slots than a 16-bit count can state.");
                }
                writer.Write((ushort)slots);
                writer.Write((ushort)structure.Properties.Count);
                int slot = 0;
                foreach (ReflectedProperty property in structure.Properties)
                {
                    if (property.ArrayDim is < 1 or > byte.MaxValue)
                    {
                        throw new InvalidDataException($"Property {structure.Name}.{property.Name} has a static array size of {property.ArrayDim}, which no usmap can state.");
                    }
                    writer.Write((ushort)slot);
                    writer.Write((byte)property.ArrayDim);
                    writer.Write(names[property.Name]);
                    WriteType(writer, names, property.Type);
                    slot += property.ArrayDim;
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream file = File.Create(path);
        using BinaryWriter header = new(file, Encoding.UTF8, leaveOpen: true);
        header.Write(Magic);
        header.Write((byte)EUsmapVersion.Latest);
        header.Write(0);
        header.Write((byte)EUsmapCompressionMethod.None);
        header.Write((uint)payload.Length);
        header.Write((uint)payload.Length);
        header.Flush();
        payload.Position = 0;
        payload.CopyTo(file);
    }

    private static void AddTypeNames(NameTable names, ReflectedType type)
    {
        if (type.StructName is not null)
        {
            names.Add(type.StructName);
        }
        if (type.EnumName is not null)
        {
            names.Add(type.EnumName);
        }
        if (type.Inner is not null)
        {
            AddTypeNames(names, type.Inner);
        }
        if (type.Value is not null)
        {
            AddTypeNames(names, type.Value);
        }
    }

    private static void WriteType(BinaryWriter writer, NameTable names, ReflectedType type)
    {
        writer.Write((byte)type.Kind);
        switch (type.Kind)
        {
            case EPropertyType.EnumProperty:
                WriteType(writer, names, type.Inner ?? throw new InvalidDataException("An enum property states no underlying type."));
                writer.Write(type.EnumName is null ? NoName : names[type.EnumName]);
                break;
            case EPropertyType.StructProperty:
                writer.Write(type.StructName is null ? NoName : names[type.StructName]);
                break;
            case EPropertyType.SetProperty:
            case EPropertyType.ArrayProperty:
            case EPropertyType.OptionalProperty:
                WriteType(writer, names, type.Inner ?? throw new InvalidDataException($"A {type.Kind} states no inner type."));
                break;
            case EPropertyType.MapProperty:
                WriteType(writer, names, type.Inner ?? throw new InvalidDataException("A map property states no key type."));
                WriteType(writer, names, type.Value ?? throw new InvalidDataException("A map property states no value type."));
                break;
        }
    }

    private sealed class NameTable
    {
        private readonly Dictionary<string, int> indices = new(StringComparer.Ordinal);
        private readonly List<string> names = new();

        public int Count => names.Count;

        public IReadOnlyList<string> Names => names;

        public int this[string name] => indices[name];

        public void Add(string name)
        {
            if (indices.TryAdd(name, names.Count))
            {
                names.Add(name);
            }
        }
    }
}
