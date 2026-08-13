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

    public static DataParam List(string name) => new(name, ParamKind.TextList, false);

    public bool Repeatable => Kind == ParamKind.TextList;

    public override string ToString() => Repeatable ? Name + "..." : Required ? Name : Name + "?";
}
