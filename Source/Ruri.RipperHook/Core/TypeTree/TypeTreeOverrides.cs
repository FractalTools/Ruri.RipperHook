using System;
using System.Collections.Generic;
using AssetRipper.IO.Endian;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

public static class TypeTreeOverrides
{
    public delegate void CapturedElementTailReader(TypeTreeValue element, ref EndianSpanReader reader);

    private static readonly Dictionary<(ClassIDType ClassID, string Path), Func<TypeTreeReadContext, bool>> Gates = new();
    private static readonly Dictionary<(ClassIDType ClassID, string Path), Delegate> ValueFixes = new();
    private static readonly Dictionary<ClassIDType, List<Action<TypeTreeReadContext>>> PostReaders = new();
    private static readonly Dictionary<(ClassIDType ClassID, string Path), CapturedElementTailReader?> Captures = new();

    public static int Revision { get; private set; }

    public static void RegisterGate(ClassIDType classID, string nodePath, Func<TypeTreeReadContext, bool> gate, IReadOnlyList<string> captures)
    {
        Gates[(classID, nodePath)] = gate;
        AddCaptures(classID, captures);
        Revision++;
    }

    public static void RegisterValueFix(ClassIDType classID, string nodePath, Delegate fix)
    {
        ValueFixes[(classID, nodePath)] = fix;
        Revision++;
    }

    public static void RegisterPostRead(ClassIDType classID, Action<TypeTreeReadContext> postRead, IReadOnlyList<string> captures)
    {
        if (!PostReaders.TryGetValue(classID, out List<Action<TypeTreeReadContext>>? list))
        {
            list = new List<Action<TypeTreeReadContext>>();
            PostReaders.Add(classID, list);
        }
        list.Add(postRead);
        AddCaptures(classID, captures);
        Revision++;
    }

    internal static void RegisterPostRead(TypeTreePostReadAttribute attribute, Action<TypeTreeReadContext> postRead)
    {
        RegisterPostRead(attribute.ClassID, postRead, attribute.Captures);
        if (attribute.CapturedElementTail is { } tail)
        {
            Captures[(attribute.ClassID, tail.Path)] = tail.Read;
        }
    }

    public static void Clear()
    {
        Gates.Clear();
        ValueFixes.Clear();
        PostReaders.Clear();
        Captures.Clear();
        Revision++;
    }

    internal static Func<TypeTreeReadContext, bool>? FindGate(ClassIDType classID, string path) =>
        Gates.TryGetValue((classID, path), out Func<TypeTreeReadContext, bool>? gate) ? gate : null;

    internal static Delegate? FindValueFix(ClassIDType classID, string path) =>
        ValueFixes.TryGetValue((classID, path), out Delegate? fix) ? fix : null;

    internal static bool TryGetCapture(ClassIDType classID, string path, out CapturedElementTailReader? readTail) =>
        Captures.TryGetValue((classID, path), out readTail);

    internal static IReadOnlyList<Action<TypeTreeReadContext>>? FindPostReaders(ClassIDType classID) =>
        PostReaders.TryGetValue(classID, out List<Action<TypeTreeReadContext>>? list) ? list : null;

    private static void AddCaptures(ClassIDType classID, IReadOnlyList<string> captures)
    {
        for (int i = 0; i < captures.Count; i++)
        {
            Captures.TryAdd((classID, captures[i]), null);
        }
    }
}
