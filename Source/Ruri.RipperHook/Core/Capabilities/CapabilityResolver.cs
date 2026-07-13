using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;
using Ruri.Hook.Utils;

namespace Ruri.RipperHook.Core.Capabilities;

/// <summary>
/// Resolves and installs every capability declared for a game at a specific engine build. A
/// capability is a static method tagged <see cref="SinceAttribute"/> plus either a
/// <c>[RetargetMethod]</c>/<c>[RetargetMethodFunc]</c>/<c>[RetargetMethodCtorFunc]</c> attribute
/// (a retarget capability, competing by the original method it patches) or a
/// <see cref="FeedsModuleAttribute"/> (a module capability, competing by its module's static
/// delegate field). Within each competing slot, the method with the highest build not exceeding
/// the resolved build wins; every other candidate in that slot is never applied.
///
/// This replaces per-version <c>AddMethodHook</c>/<c>RegisterModule</c> call sites entirely for a
/// migrated game: adding a version that changes nothing needs no new call site at all (its build
/// number simply resolves to the same set of winners as before); one that changes one thing needs
/// exactly one new tagged method sharing the slot of what it replaces.
///
/// Capability authors must always pass an explicit source method name to
/// <c>[RetargetMethod]</c>/<c>[RetargetMethodFunc]</c> -- the name-inference convenience those
/// attributes support (stripping a type-name prefix off the tagged method's own name) is meant for
/// a single hand-written call site, not for grouping many same-slot capabilities by name, so it is
/// deliberately not replicated here.
/// </summary>
public static class CapabilityResolver
{
    public static void Apply(GameType game, int engineBuild, HookRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        List<MethodInfo> capabilityMethods = DiscoverCapabilityMethods(game);

        ApplyRetargetCapabilities(capabilityMethods, engineBuild, registry);
        ApplyModuleCapabilities(capabilityMethods, engineBuild, registry);
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

    private static void ApplyRetargetCapabilities(List<MethodInfo> methods, int engineBuild, HookRegistry registry)
    {
        // Each retarget attribute is its own slot. A method carrying several (one handler for two
        // AnimationClip type-versions, one version getter answering both DefaultVersion and
        // TargetVersion) contributes one slot per attribute and, in every case in this codebase,
        // wins all of them -- nothing else competes for its slots -- so the winning methods, applied
        // once each via ApplyManualHooks (which installs all of a method's attributes), reproduce the
        // hand-written set exactly. The last-wins dedup guard in ReflectionExtensions is the backstop
        // if a future method ever won only some of its slots.
        List<MethodInfo> winners = methods
            .SelectMany(m => RetargetSlots(m).Select(slot => (Slot: slot, Method: m)))
            .GroupBy(static entry => entry.Slot)
            .Select(slot => ResolveWinner(slot.Select(static e => e.Method), engineBuild))
            .Where(static m => m is not null)
            .Select(static m => m!)
            .Distinct()
            .ToList();

        if (winners.Count > 0)
        {
            registry.ApplyManualHooks(winners);
        }
    }

    // The natural competing key for a retarget capability is the original method it patches --
    // two capabilities only ever compete when they target the exact same source method. A method
    // with several retarget attributes yields one slot per attribute (it patches several originals).
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

    private static void ApplyModuleCapabilities(List<MethodInfo> methods, int engineBuild, HookRegistry registry)
    {
        var slots = methods
            .Where(static m => m.GetCustomAttribute<FeedsModuleAttribute>() is not null)
            .GroupBy(static m => m.GetCustomAttribute<FeedsModuleAttribute>()!, ModuleSlotComparer.Instance);

        HashSet<Type> trampolineInstalled = new();

        foreach (var slot in slots)
        {
            MethodInfo? winner = ResolveWinner(slot, engineBuild);
            if (winner is null) continue;

            Type moduleType = slot.Key.ModuleType;
            if (trampolineInstalled.Add(moduleType))
            {
                // The module's own [RetargetMethod] instance method is a fixed shim -- it only
                // ever needs installing once per resolved game; only the delegate it reads varies.
                registry.ApplyTypeHooks(moduleType);
            }

            FieldInfo field = moduleType.GetField(slot.Key.StaticFieldName, ReflectionExtensions.PublicStaticBindFlag())
                ?? throw new InvalidOperationException(
                    $"[CapabilityResolver] {moduleType.Name} has no public static field '{slot.Key.StaticFieldName}'.");

            Delegate implementation = Delegate.CreateDelegate(field.FieldType, winner);
            field.SetValue(null, implementation);
        }
    }

    private static MethodInfo? ResolveWinner(IEnumerable<MethodInfo> candidates, int engineBuild) =>
        candidates
            .Where(m => m.GetCustomAttribute<SinceAttribute>()!.Build <= engineBuild)
            .OrderByDescending(m => m.GetCustomAttribute<SinceAttribute>()!.Build)
            .FirstOrDefault();

    private sealed class ModuleSlotComparer : IEqualityComparer<FeedsModuleAttribute>
    {
        public static readonly ModuleSlotComparer Instance = new();

        public bool Equals(FeedsModuleAttribute? x, FeedsModuleAttribute? y) =>
            x is not null && y is not null && x.ModuleType == y.ModuleType && x.StaticFieldName == y.StaticFieldName;

        public int GetHashCode(FeedsModuleAttribute obj) => HashCode.Combine(obj.ModuleType, obj.StaticFieldName);
    }
}
