namespace Ruri.RipperHook.Data;

public enum ParamKind
{
    Text,
    Integer,
    Real,
    Flag,
    TextList,
}

public sealed record DataParam(string Name, ParamKind Kind, bool Required)
{
    public static DataParam Text(string name, bool required = true) => new(name, ParamKind.Text, required);

    public static DataParam Integer(string name, bool required = true) => new(name, ParamKind.Integer, required);

    public static DataParam Real(string name, bool required = true) => new(name, ParamKind.Real, required);

    public static DataParam Flag(string name, bool required = true) => new(name, ParamKind.Flag, required);

    /// <summary>A repeatable argument. ``required`` says whether stating NONE of it is an answer:
    /// a list of seeds a caller may leave empty is optional, while a list of archives to look
    /// inside has nothing to say when it is empty and is required.</summary>
    public static DataParam List(string name, bool required = false) =>
        new(name, ParamKind.TextList, required);

    public bool Repeatable => Kind == ParamKind.TextList;

    /// <summary>The signature a surface reads to tell a list it can OPEN from an answer about
    /// something not picked yet: <c>name</c> needs a value, <c>name?</c> may be left out,
    /// <c>name...</c> is a list that may be empty, <c>name+</c> is one that may not.</summary>
    public override string ToString() => Repeatable
        ? Required ? Name + "+" : Name + "..."
        : Required ? Name : Name + "?";
}
