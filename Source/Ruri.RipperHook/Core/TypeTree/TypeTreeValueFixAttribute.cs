using System;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class TypeTreeValueFixAttribute : Attribute
{
    public ClassIDType ClassID { get; }

    public string NodePath { get; }

    public TypeTreeValueFixAttribute(ClassIDType classID, string nodePath)
    {
        ClassID = classID;
        NodePath = nodePath;
    }
}
