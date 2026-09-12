using System;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class TypeTreePostReadAttribute : Attribute
{
    public ClassIDType ClassID { get; }

    public string Slot { get; set; } = "";

    public string[] Captures { get; set; } = Array.Empty<string>();

    public TypeTreePostReadAttribute(ClassIDType classID)
    {
        ClassID = classID;
    }
}
