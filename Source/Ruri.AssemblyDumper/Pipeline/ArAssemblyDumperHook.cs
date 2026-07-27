using AssetRipper.AssemblyDumper;
using AssetRipper.AssemblyDumper.Groups;
using AssetRipper.AssemblyDumper.Passes;
using AssetRipper.DocExtraction.DataStructures;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using System.Reflection;

namespace Ruri.AssemblyDumper.Pipeline;

/// <summary>
/// DIAGNOSTIC build: closest-version hooks re-enabled but instrumented to log every gap
/// ([GAP] group@reqVersion -> fallbackVersion). Used to enumerate exactly which (group, version)
/// pairs the fallback bridges, so the missing real-Unity coverage can be added to Common and the
/// hooks removed for good. Pass506 handled by TpkBuilder (drops overlay 310). Pass039 = docs prune.
/// </summary>
internal sealed class ArAssemblyDumperHook : RuriHook
{
    public new void Initialize() => InitAttributeHook();

    private static readonly HashSet<string> _loggedGaps = new();
    private static void LogGap(string group, UnityVersion req, UnityVersion to)
    {
        string k = $"{group}@{req}->{to}";
        if (_loggedGaps.Add(k)) Console.WriteLine($"[GAP] {k}");
    }

    [RetargetMethod(typeof(SharedState), "GetGeneratedInstanceForObjectType")]
    private static GeneratedClassInstance SharedState_GetGeneratedInstanceForObjectType(
        SharedState self, string typeName, UnityVersion version)
    {
        if (!self.NameToTypeID.TryGetValue(typeName, out HashSet<int>? list))
            throw new Exception($"Could not find {typeName} in the name dictionary");

        GeneratedClassInstance? closest = null;
        UnityVersion closestStart = default;
        foreach (int id in list)
        {
            ClassGroup group = self.ClassGroups[id];
            foreach (GeneratedClassInstance instance in group.Instances)
            {
                if (instance.Name != typeName) continue;
                if (instance.VersionRange.Contains(version)) return instance;
                if (instance.VersionRange.Start <= version &&
                    (closest is null || instance.VersionRange.Start > closestStart))
                {
                    closest = instance;
                    closestStart = instance.VersionRange.Start;
                }
            }
        }
        if (closest is not null) { LogGap($"obj:{typeName}", version, closestStart); return closest; }
        throw new Exception($"Could not find type {typeName} on version {version}");
    }

    [RetargetMethod(typeof(ClassGroupBase), nameof(ClassGroupBase.GetTypeForVersion))]
    private static TypeDefinition ClassGroupBase_GetTypeForVersion(ClassGroupBase self, UnityVersion version)
    {
        UnityVersion compatible = GetCompatibleVersion(self, version);
        if (compatible != version) LogGap(self.Name, version, compatible);
        return self.GetInstanceForVersion(compatible).Type;
    }

    private static UnityVersion GetCompatibleVersion(ClassGroupBase group, UnityVersion version)
    {
        UnityVersion? closest = null;
        foreach (GeneratedClassInstance instance in group.Instances)
        {
            if (instance.VersionRange.Contains(version)) return version;
            if (instance.VersionRange.Start <= version) closest = instance.VersionRange.Start;
            else break;
        }
        return closest ?? group.MinimumVersion;
    }

    [RetargetMethod(typeof(Pass039_InjectEnumValues), nameof(Pass039_InjectEnumValues.DoPass), true, false)]
    private static void Pass039_InjectEnumValues_DoPass()
    {
        try { PruneInjectedDocumentation(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[ArAssemblyDumperHook/Pass039] prune failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void PruneInjectedDocumentation()
    {
        FieldInfo? field = typeof(Pass039_InjectEnumValues).GetField(
            "injectedDocumentation", BindingFlags.NonPublic | BindingFlags.Static);
        if (field is null) return;

        var injectedDoc = (Dictionary<string, List<(string?, string)>>)field.GetValue(null)!;
        Dictionary<string, EnumHistory> enums = SharedState.Instance.HistoryFile.Enums;

        foreach (string key in injectedDoc.Keys.ToList())
        {
            if (!enums.TryGetValue(key, out EnumHistory? history)) { injectedDoc.Remove(key); continue; }
            List<(string?, string)> list = injectedDoc[key];
            list.RemoveAll(item => item.Item1 is not null && !history.Members.ContainsKey(item.Item1));
        }
    }
}
