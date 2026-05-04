using System;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class TypeTreeHookAttribute : Attribute
{
    public ClassIDType ClassID { get; }
    public string? GeneratedAssemblyNamespace { get; set; }

    public TypeTreeHookAttribute(ClassIDType classID)
    {
        ClassID = classID;
    }
}
