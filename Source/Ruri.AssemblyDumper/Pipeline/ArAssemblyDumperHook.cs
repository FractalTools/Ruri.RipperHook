using AssetRipper.AssemblyDumper;
using AssetRipper.AssemblyDumper.Groups;
using AssetRipper.AssemblyDumper.Passes;
using AssetRipper.AssemblyDumper.Utils;
using AssetRipper.DocExtraction.DataStructures;
using AssetRipper.Primitives;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using AsmResolver.DotNet;
using System.Reflection;

namespace Ruri.AssemblyDumper.Pipeline;

/// <summary>
/// 1:1 复刻 AssetRipper.AssemblyDumper 当前 9 文件 local diff，使用 Ruri.Hook 框架的
/// <see cref="RetargetMethodAttribute"/> 把 hook 方法挂到对应的 AR 源方法上。
/// 配合 <see cref="PostProcess.RenameAssemblyAndNamespaces"/>（处理 const AssemblyName）后，
/// AssetRipper.AssemblyDumper 子模块可保持上游干净（CLAUDE.md frozen-area 规则）。
/// </summary>
internal sealed class ArAssemblyDumperHook : RuriHook
{
    public new void Initialize() => InitAttributeHook();

    // ------------------------------------------------------------------
    // SharedState.GetGeneratedInstanceForObjectType  ←  SharedState.cs diff
    //   closest-version fallback when no exact match exists
    // ------------------------------------------------------------------
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
        return closest ?? throw new Exception(
            $"Could not find type {typeName} on version {version} (及更早的历史版本均未匹配)");
    }

    // ------------------------------------------------------------------
    // ClassGroupBase.GetTypeForVersion  ←  GenericTypeResolver.cs / Pass100 / Pass101 / UniqueNameFactory diff
    //   Hook this single method so every caller transparently gets closest-version
    //   substitution. The four diff sites all call subclassGroup.GetTypeForVersion.
    // ------------------------------------------------------------------
    [RetargetMethod(typeof(ClassGroupBase), nameof(ClassGroupBase.GetTypeForVersion))]
    private static TypeDefinition ClassGroupBase_GetTypeForVersion(ClassGroupBase self, UnityVersion version)
    {
        UnityVersion compatible = GetCompatibleVersion(self, version);
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

    // ------------------------------------------------------------------
    // Pass005_SplitAbstractClasses.GetClass  ←  Pass005_SplitAbstractClasses.cs diff
    //   exact-match → fallback to closest start version (preferring earlier)
    // ------------------------------------------------------------------
    [RetargetMethod(typeof(Pass005_SplitAbstractClasses), "GetClass")]
    private static UniversalClass Pass005_SplitAbstractClasses_GetClass(string name, UnityVersion version)
    {
        var ss = SharedState.Instance;
        if (!ss.NameToTypeID.TryGetValue(name, out HashSet<int>? idSet))
            throw new InvalidOperationException($"Could not find class {name} for {version}");

        List<UniversalClass> exact = new();
        foreach (int id in idSet)
        {
            VersionedList<UniversalClass> list = ss.ClassInformation[id];
            UniversalClass? raw = list.GetItemForVersion(version);
            if (raw is not null && raw.Name == name) exact.Add(raw);
        }
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw new InvalidOperationException($"Multiple exact matches found for {name} on {version}");

        var earlier = new List<(UnityVersion start, UniversalClass cls)>();
        var later = new List<(UnityVersion start, UniversalClass cls)>();
        foreach (int id in idSet)
        {
            VersionedList<UniversalClass> list = ss.ClassInformation[id];
            foreach ((UnityVersion start, UniversalClass? cls) in list)
            {
                if (cls is null || cls.Name != name) continue;
                if (start <= version) earlier.Add((start, cls));
                else later.Add((start, cls));
            }
        }
        if (earlier.Count > 0)
        {
            earlier.Sort((a, b) => b.start.CompareTo(a.start));
            return earlier[0].cls;
        }
        if (later.Count > 0)
        {
            later.Sort((a, b) => a.start.CompareTo(b.start));
            return later[0].cls;
        }
        throw new InvalidOperationException($"Could not find class {name} for {version}");
    }

    // ------------------------------------------------------------------
    // Pass039_InjectEnumValues.DoPass  ←  Pass039_InjectEnumValues.cs diff
    //   diff 在 doc-injection 块用 TryGetValue 替换 dictionary[key]。我们没法 hook
    //   闭包内的 dict 访问，所以在 orig() 之前 prune 已知会缺失的条目，让原 DoPass
    //   只看到安全的输入。isBefore=true, isReturn=false → prefix-run prune 后继续。
    // ------------------------------------------------------------------
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

        int prunedTypes = 0, prunedMembers = 0;
        foreach (string key in injectedDoc.Keys.ToList())
        {
            if (!enums.TryGetValue(key, out EnumHistory? history))
            {
                Console.WriteLine($"[InjectDocumentation] 警告: 未找到枚举类型 {key}，跳过文档注入。");
                injectedDoc.Remove(key);
                prunedTypes++;
                continue;
            }
            List<(string?, string)> list = injectedDoc[key];
            int before = list.Count;
            list.RemoveAll(item =>
            {
                if (item.Item1 is null) return false;
                bool missing = !history.Members.ContainsKey(item.Item1);
                if (missing) Console.WriteLine($"[InjectDocumentation] 警告: {key} 中不存在成员 {item.Item1}，跳过文档注入。");
                return missing;
            });
            prunedMembers += before - list.Count;
        }
        if (prunedTypes > 0 || prunedMembers > 0)
        {
            Console.WriteLine($"[ArAssemblyDumperHook/Pass039] Pruned {prunedTypes} missing enum types and {prunedMembers} missing members.");
        }
    }

    // ------------------------------------------------------------------
    // Pass506_FixUnityConnectSettings.DoPass  ←  Pass506_FixUnityConnectSettings.cs diff
    //   prepend `return;` ⇒ no-op replacement
    // ------------------------------------------------------------------
    [RetargetMethod(typeof(Pass506_FixUnityConnectSettings), nameof(Pass506_FixUnityConnectSettings.DoPass))]
    private static void Pass506_FixUnityConnectSettings_DoPass() { }

    // ------------------------------------------------------------------
    // Pass555_CreateCommonString.ThrowIfStringCountIsWrong  ←  Pass555_CreateCommonString.cs diff
    //   change expected count 113 → 112
    // ------------------------------------------------------------------
    [RetargetMethod(typeof(Pass555_CreateCommonString), "ThrowIfStringCountIsWrong")]
    private static void Pass555_CreateCommonString_ThrowIfStringCountIsWrong()
    {
        int count = SharedState.Instance.CommonString.Strings.Count;
        if (count != 112)
            throw new Exception($"The size of Common String has changed! {count}");
    }
}
