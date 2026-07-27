using System;
using System.Collections.Generic;
using AssetRipper.SourceGenerated;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// The per-game table of type tree deviations the tpk cannot express: conditional nodes, value
/// rewrites, and post-read decoders. Filled by <c>CapabilityResolver</c> from
/// <see cref="TypeTreeNodeGateAttribute"/> / <see cref="TypeTreeValueFixAttribute"/> /
/// <see cref="TypeTreePostReadAttribute"/> so a version only has to declare what it changes.
///
/// One game hook is resolved per process (same assumption the rest of the hook system makes), so
/// this is a single flat table rather than a per-game one.
/// </summary>
public static class TypeTreeOverrides
{
    private static readonly Dictionary<(ClassIDType ClassID, string Path), Func<TypeTreeReadContext, bool>> Gates = new();
    private static readonly Dictionary<(ClassIDType ClassID, string Path), Delegate> ValueFixes = new();
    private static readonly Dictionary<ClassIDType, List<Action<TypeTreeReadContext>>> PostReaders = new();
    private static readonly HashSet<(ClassIDType ClassID, string Path)> Captures = new();

    /// <summary>Bumped whenever the table changes so cached read plans rebuild instead of going stale.</summary>
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

    internal static bool ShouldCapture(ClassIDType classID, string path) => Captures.Contains((classID, path));

    internal static IReadOnlyList<Action<TypeTreeReadContext>>? FindPostReaders(ClassIDType classID) =>
        PostReaders.TryGetValue(classID, out List<Action<TypeTreeReadContext>>? list) ? list : null;

    private static void AddCaptures(ClassIDType classID, IReadOnlyList<string> captures)
    {
        for (int i = 0; i < captures.Count; i++)
        {
            Captures.Add((classID, captures[i]));
        }
    }
}
