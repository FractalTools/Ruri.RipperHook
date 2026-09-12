using CUE4Parse.MappingsProvider;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// The statement that no reflection schema has been given for this install. CUE4Parse refuses
/// to open a package whose properties are unversioned unless SOME schema exists; with this one
/// it opens every package's header (names, imports, exports, classes -- what a scan needs) and
/// fails only where an object's properties would have to be read, which is exactly the part a
/// missing .usmap makes unreadable.
/// </summary>
public sealed class SchemalessMappingsProvider : AbstractTypeMappingsProvider
{
    public override TypeMappings? MappingsForGame { get; protected set; } = new();

    public override void Load(string path, StringComparer? comparer = null)
    {
    }

    public override void Load(byte[] bytes, StringComparer? comparer = null)
    {
    }

    public override void Reload()
    {
    }
}
