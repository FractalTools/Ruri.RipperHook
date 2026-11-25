using System;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public class TypeTreePostReadAttribute : Attribute
{
    public ClassIDType ClassID { get; }

    public string Slot { get; set; } = "";

    public string[] Captures { get; set; } = Array.Empty<string>();

    internal (string Path, TypeTreeOverrides.CapturedElementTailReader Read)? CapturedElementTail { get; }

    public TypeTreePostReadAttribute(ClassIDType classID)
    {
        ClassID = classID;
    }

    protected TypeTreePostReadAttribute(ClassIDType classID, string capturedSequence,
        TypeTreeOverrides.CapturedElementTailReader readElementTail)
        : this(classID)
    {
        CapturedElementTail = (capturedSequence, readElementTail);
    }
}
