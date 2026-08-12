using System;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class TypeTreeNodeGateAttribute : Attribute
{
    public ClassIDType ClassID { get; }

    public string NodePath { get; }

    public string[] Captures { get; set; } = Array.Empty<string>();

    public TypeTreeNodeGateAttribute(ClassIDType classID, string nodePath)
    {
        ClassID = classID;
        NodePath = nodePath;
    }
}
