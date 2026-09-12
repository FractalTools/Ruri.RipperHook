namespace Ruri.RipperHook.Tables;

[Flags]
public enum ColumnRole
{
    None = 0,
    Label = 1 << 0,
    Key = 1 << 1,
    Detail = 1 << 2,
    Group = 1 << 3,
    Facet = 1 << 4,
    Named = 1 << 5,
    Shipped = 1 << 6,
    Payload = 1 << 7,
}
