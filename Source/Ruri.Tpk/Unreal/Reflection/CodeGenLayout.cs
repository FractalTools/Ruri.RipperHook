using AsmResolver.Symbols.Pdb.Leaves;

namespace Ruri.Tpk.Unreal.Reflection;

/// <summary>
/// Where each member of one code-gen structure lies, as the compiler laid it out for this very
/// build: offsets and bit fields read off the type record, looked up by the member names the
/// engine header spells. A member the record lacks is an error naming both.
/// </summary>
internal sealed class CodeGenLayout
{
    public readonly record struct Member(uint Offset, int BitPosition, int BitLength)
    {
        public bool IsBitField => BitPosition >= 0;
    }

    private readonly Dictionary<string, Member> members = new(StringComparer.Ordinal);

    private CodeGenLayout(string name, uint size)
    {
        Name = name;
        Size = size;
    }

    public string Name { get; }

    public uint Size { get; }

    public static CodeGenLayout From(ClassTypeRecord record)
    {
        CodeGenLayout layout = new(record.Name!.Value, (uint)record.Size);
        foreach (CodeViewField field in record.Fields!.Entries)
        {
            if (field is not InstanceDataField data || data.Name is null)
            {
                continue;
            }
            layout.members[data.Name.Value] = data.DataType is BitFieldTypeRecord bits
                ? new Member((uint)data.Offset, bits.Position, bits.Length)
                : new Member((uint)data.Offset, -1, 0);
        }
        return layout;
    }

    public uint Offset(string member) => Get(member).Offset;

    public Member Get(string member) =>
        members.TryGetValue(member, out Member found)
            ? found
            : throw new InvalidDataException($"{Name} has no member '{member}' in the program database.");
}
