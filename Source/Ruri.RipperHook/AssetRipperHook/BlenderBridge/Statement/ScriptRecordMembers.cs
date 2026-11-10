namespace Ruri.RipperHook.BlenderBridge.Statements;

/// <summary>The names a script record states itself by, and the kinds of its rows and references: the one place they are
/// written, so that the record and whoever reads it are cut from the same words. The fields of a script are not here -- they
/// carry the names the game's own scripts give them.</summary>
public static class ScriptRecordMembers
{
    public const string Key = "key";
    public const string Root = "root";
    public const string Path = "path";
    public const string PathOrdinal = "pathOrdinal";
    public const string Class = "class";
    public const string Namespace = "namespace";
    public const string Assembly = "assembly";
    public const string ClassOrdinal = "classOrdinal";
    public const string Enabled = "enabled";
    public const string Name = "name";
    public const string Container = "container";
    public const string Fields = "fields";
    public const string Json = "json";
    public const string Unreadable = "unreadable";
    public const string Kind = "kind";

    public const string ComponentRow = "component";
    public const string AssetRow = "asset";

    public const string NodeReference = "node";
    public const string AssetReference = "asset";
    public const string NoReference = "none";

    public const string EnabledField = "m_Enabled";
}
