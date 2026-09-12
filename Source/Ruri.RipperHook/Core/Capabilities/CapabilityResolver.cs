using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;
using Ruri.Hook.Utils;
using Ruri.RipperHook.Core.TypeTree;

namespace Ruri.RipperHook.Core.Capabilities;

public static class CapabilityResolver
{
    public static void Apply(GameType game, string version, HookRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        List<MethodInfo> capabilityMethods = DiscoverCapabilityMethods(game);

        ApplyRetargetCapabilities(capabilityMethods, version, registry);
        ApplyModuleCapabilities(capabilityMethods, version, registry);
        ApplyTypeTreeCapabilities(capabilityMethods, version);
    }

    private static void ApplyTypeTreeCapabilities(List<MethodInfo> methods, string version)
    {
        TypeTreeOverrides.Clear();

        var gateSlots = methods
            .SelectMany(m => m.GetCustomAttributes<TypeTreeNodeGateAttribute>().Select(a => (Attribute: a, Method: m)))
            .GroupBy(static e => (e.Attribute.ClassID, e.Attribute.NodePath));
        foreach (var slot in gateSlots)
        {
            MethodInfo? winner = ResolveWinner(slot.Select(static e => e.Method), version);
            if (winner is null) continue;

            TypeTreeNodeGateAttribute attribute = slot.First(e => e.Method == winner).Attribute;
            TypeTreeOverrides.RegisterGate(
                attribute.ClassID,
                attribute.NodePath,
                winner.CreateDelegate<Func<TypeTreeReadContext, bool>>(),
                attribute.Captures);
        }

        var valueFixSlots = methods
            .SelectMany(m => m.GetCustomAttributes<TypeTreeValueFixAttribute>().Select(a => (Attribute: a, Method: m)))
            .GroupBy(static e => (e.Attribute.ClassID, e.Attribute.NodePath));
        foreach (var slot in valueFixSlots)
        {
            MethodInfo? winner = ResolveWinner(slot.Select(static e => e.Method), version);
            if (winner is null) continue;

            ParameterInfo[] parameters = winner.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != winner.ReturnType)
            {
                throw new InvalidOperationException(
                    $"[CapabilityResolver] {winner.DeclaringType?.Name}.{winner.Name} must be a T Fix(T value) to be a type tree value fix.");
            }

            TypeTreeValueFixAttribute attribute = slot.First(e => e.Method == winner).Attribute;
            TypeTreeOverrides.RegisterValueFix(
                attribute.ClassID,
                attribute.NodePath,
                winner.CreateDelegate(typeof(Func<,>).MakeGenericType(winner.ReturnType, winner.ReturnType)));
        }

        var postReadSlots = methods
            .SelectMany(m => m.GetCustomAttributes<TypeTreePostReadAttribute>().Select(a => (Attribute: a, Method: m)))
            .GroupBy(static e => (e.Attribute.ClassID, e.Attribute.Slot));
        foreach (var slot in postReadSlots)
        {
            MethodInfo? winner = ResolveWinner(slot.Select(static e => e.Method), version);
            if (winner is null) continue;

            TypeTreePostReadAttribute attribute = slot.First(e => e.Method == winner).Attribute;
            TypeTreeOverrides.RegisterPostRead(
                attribute.ClassID,
                winner.CreateDelegate<Action<TypeTreeReadContext>>(),
                attribute.Captures);
        }
    }

    private static List<MethodInfo> DiscoverCapabilityMethods(GameType game)
    {
        List<MethodInfo> methods = new();

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? name = assembly.GetName().Name;
            if (name is null) continue;
            if (name.StartsWith("System.", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("WindowsBase", StringComparison.Ordinal) ||
                name.Equals("PresentationCore", StringComparison.Ordinal) ||
                name.Equals("PresentationFramework", StringComparison.Ordinal))
            {
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException tle)
            {
                types = tle.Types.Where(static t => t != null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (Type type in types)
            {
                if (type is null) continue;

                bool ownsThisGame = type.GetCustomAttributes<GameCapabilitiesAttribute>()
                    .Any(a => a.GameType == game);
                if (!ownsThisGame) continue;

                methods.AddRange(type.GetMethods(ReflectionExtensions.AnyBindFlag())
                    .Where(static m => m.GetCustomAttribute<SinceAttribute>() is not null));
            }
        }

        return methods;
    }

    private static void ApplyRetargetCapabilities(List<MethodInfo> methods, string version, HookRegistry registry)
    {
        List<MethodInfo> winners = methods
            .SelectMany(m => RetargetSlots(m).Select(slot => (Slot: slot, Method: m)))
            .GroupBy(static entry => entry.Slot)
            .Select(slot => ResolveWinner(slot.Select(static e => e.Method), version))
            .Where(static m => m is not null)
            .Select(static m => m!)
            .Distinct()
            .ToList();

        if (winners.Count > 0)
        {
            registry.ApplyManualHooks(winners);
        }
    }

    private static IEnumerable<(string SourceType, string MethodName)> RetargetSlots(MethodInfo method)
    {
        foreach (RetargetMethodAttribute retarget in method.GetCustomAttributes<RetargetMethodAttribute>())
        {
            yield return (
                retarget.SourceType?.FullName ?? retarget.SourceTypeName ?? throw MissingSourceType(method),
                retarget.SourceMethodName ?? throw MissingSourceMethodName(method));
        }
        foreach (RetargetMethodFuncAttribute retargetFunc in method.GetCustomAttributes<RetargetMethodFuncAttribute>())
        {
            yield return (
                retargetFunc.SourceType.FullName ?? throw MissingSourceType(method),
                retargetFunc.SourceMethodName ?? throw MissingSourceMethodName(method));
        }
        foreach (RetargetMethodCtorFuncAttribute ctorFunc in method.GetCustomAttributes<RetargetMethodCtorFuncAttribute>())
        {
            yield return (ctorFunc.SourceType.FullName ?? throw MissingSourceType(method), ".ctor");
        }
    }

    private static InvalidOperationException MissingSourceType(MethodInfo method) =>
        new($"[CapabilityResolver] {method.DeclaringType?.Name}.{method.Name} has no resolvable source type.");

    private static InvalidOperationException MissingSourceMethodName(MethodInfo method) =>
        new($"[CapabilityResolver] {method.DeclaringType?.Name}.{method.Name} must pass an explicit source method name " +
            "-- name inference is not supported for capability slots (it would make two capabilities that infer " +
            "to the same target invisible to each other).");

    private static void ApplyModuleCapabilities(List<MethodInfo> methods, string version, HookRegistry registry)
    {
        var slots = methods
            .Where(static m => m.GetCustomAttribute<FeedsModuleAttribute>() is not null)
            .GroupBy(static m => m.GetCustomAttribute<FeedsModuleAttribute>()!, ModuleSlotComparer.Instance);

        HashSet<Type> trampolineInstalled = new();

        foreach (var slot in slots)
        {
            MethodInfo? winner = ResolveWinner(slot, version);
            if (winner is null) continue;

            Type moduleType = slot.Key.ModuleType;
            if (trampolineInstalled.Add(moduleType))
            {
                registry.ApplyTypeHooks(moduleType);
            }

            FieldInfo field = moduleType.GetField(slot.Key.StaticFieldName, ReflectionExtensions.PublicStaticBindFlag())
                ?? throw new InvalidOperationException(
                    $"[CapabilityResolver] {moduleType.Name} has no public static field '{slot.Key.StaticFieldName}'.");

            Delegate implementation = Delegate.CreateDelegate(field.FieldType, winner);
            field.SetValue(null, implementation);
        }
    }

    private static MethodInfo? ResolveWinner(IEnumerable<MethodInfo> candidates, string version) =>
        candidates
            .Where(m => VersionKey.Compare(m.GetCustomAttribute<SinceAttribute>()!.Version, version) <= 0)
            .OrderByDescending(m => m.GetCustomAttribute<SinceAttribute>()!.Version, SinceVersionComparer.Instance)
            .FirstOrDefault();

    private sealed class SinceVersionComparer : IComparer<string>
    {
        public static readonly SinceVersionComparer Instance = new();

        public int Compare(string? x, string? y) => VersionKey.Compare(x ?? "", y ?? "");
    }

    private sealed class ModuleSlotComparer : IEqualityComparer<FeedsModuleAttribute>
    {
        public static readonly ModuleSlotComparer Instance = new();

        public bool Equals(FeedsModuleAttribute? x, FeedsModuleAttribute? y) =>
            x is not null && y is not null && x.ModuleType == y.ModuleType && x.StaticFieldName == y.StaticFieldName;

        public int GetHashCode(FeedsModuleAttribute obj) => HashCode.Combine(obj.ModuleType, obj.StaticFieldName);
    }
}
