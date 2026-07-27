using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using Ruri.Hook;
using Ruri.RipperHook.Core;
using Ruri.RipperHook.Core.Capabilities;
using Ruri.RipperHook.Core.TypeTree;

namespace Ruri.RipperHook;

public abstract class RipperHookCommon : RuriHook
{
    // Re-expose for compatibility
    public delegate void ReadReleaseDelegate(object asset, ref EndianSpanReader reader);

    private List<IHookModule> _modules = new();

    protected RipperHookCommon()
    {
    }

    public override void Initialize()
    {
        base.Initialize(); // Calls InitAttributeHook
        ProcessGameHooks();

        var ripperHookAttr = GetType().GetCustomAttribute<RipperHookAttribute>();
        if (ripperHookAttr != null)
        {
            RuriRuntimeHook.RegisterLoadedGameHook(ripperHookAttr.GameType);
        }
    }

    protected void RegisterModule(IHookModule module)
    {
        _modules.Add(module);
        module.OnApply();
        Registry.ApplyTypeHooks(module.GetType());
    }

    /// <summary>
    /// Resolves and installs every <see cref="SinceAttribute"/>-tagged capability declared for
    /// <paramref name="game"/> at <paramref name="engineBuild"/> (see
    /// <see cref="CapabilityResolver"/>) -- the data-driven replacement for hand-listing
    /// AddMethodHook/RegisterModule calls per version. A version whose resolved capability set is
    /// identical to another's needs no call of its own beyond this one.
    /// </summary>
    protected void ApplyCapabilities(GameType game, int engineBuild) => CapabilityResolver.Apply(game, engineBuild, Registry);

    protected override void InitAttributeHook()
    {
        base.InitAttributeHook();
        // Custom RipperHook logic can go here if needed
    }

    /// <summary>
    /// Scans for [TypeTreeHook] attributes on the current class and registers them.
    /// </summary>
    protected void ProcessGameHooks()
    {
        var type = GetType();
        var ripperHookAttr = type.GetCustomAttribute<RipperHookAttribute>();
        if (ripperHookAttr == null) return;

        // TypeTreeHookAttribute is AssetRipper specific
        var hookClassAttrs = type.GetCustomAttributes<TypeTreeHookAttribute>();
        if (!hookClassAttrs.Any())
        {
            return;
        }

        HookLogger.LogRaw($"    Found {hookClassAttrs.Count()} TypeTreeHook attributes in {type.Name}.");

        var classIds = hookClassAttrs.Select(a => a.ClassID).ToList();

        UnityVersion targetVersionVec = GetTargetVersion(ripperHookAttr);
        if (targetVersionVec == default) return; // Skip if version resolution failed or returned empty

        HookClasses(classIds, ripperHookAttr.BaseEngineVersion, targetVersionVec);
    }

    protected virtual UnityVersion GetTargetVersion(RipperHookAttribute attr)
    {
        return UnityVersion.Parse(attr.BaseEngineVersion);
    }

    /// <summary>
    /// Retargets every listed class's <c>ReadRelease</c> onto <see cref="HookDispatcher"/>, which
    /// reads it with the game's own type tree at <paramref name="targetVersion"/> (see
    /// <see cref="TypeTreeReadPlan"/>).
    ///
    /// <paramref name="sourceUnityVersion"/> only picks *which* stock AssetRipper class version the
    /// game's files instantiate -- the layout always comes from the tpk.
    /// </summary>
    protected void HookClasses(
        IEnumerable<ClassIDType> classIds,
        string sourceUnityVersion,
        UnityVersion targetVersion,
        Dictionary<ClassIDType, ReadReleaseDelegate>? customCallbacks = null)
    {
        Dictionary<ClassIDType, HookDispatcher.ReadReleaseDelegate>? coreCallbacks = null;
        if (customCallbacks != null)
        {
            coreCallbacks = new Dictionary<ClassIDType, HookDispatcher.ReadReleaseDelegate>();
            foreach (var kvp in customCallbacks)
            {
                coreCallbacks[kvp.Key] = (obj, ref reader) => kvp.Value(obj, ref reader);
            }
        }

        UnityVersion lookupVersion = UnityVersion.Parse(sourceUnityVersion);

        var universalDestMethod = typeof(HookDispatcher).GetMethod(nameof(HookDispatcher.Universal_ReadRelease), BindingFlags.Public | BindingFlags.Static);
        if (universalDestMethod == null) throw new Exception("Universal_ReadRelease missing");

        var originalAssembly = typeof(ClassIDType).Assembly;

        foreach (var classId in classIds)
        {
            try
            {
                Type sourceType = ResolveSourceType(originalAssembly, classId, lookupVersion);

                HookDispatcher.ReadReleaseDelegate? callback = null;
                if (coreCallbacks != null && coreCallbacks.TryGetValue(classId, out var customAction))
                {
                    callback = customAction;
                }

                if (callback == null && TypeTreeDatabase.GetReleaseRoot(classId, targetVersion) == null)
                {
                    HookLogger.LogFailure($"[-] Failed {classId}: no type tree at {targetVersion} in {TypeTreeDatabase.BlobOrigin}");
                    continue;
                }

                HookDispatcher.Register(sourceType, classId, targetVersion, callback);

                var readReleaseMethod = sourceType.GetMethod("ReadRelease", BindingFlags.Public | BindingFlags.Instance);
                if (readReleaseMethod != null)
                {
                    ReflectionExtensions.RetargetCall(readReleaseMethod, universalDestMethod, 1, true, true);
                    HookLogger.LogSuccessRaw($"    [+] Hooked {sourceType.Name} -> type tree {classId}@{targetVersion}");
                }
                else
                {
                    HookLogger.LogSuccess($"[+] {sourceType.Name} (Dispatch Only)");
                }
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"[-] Failed {classId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Finds the stock AssetRipper class the loader will instantiate for <paramref name="classId"/> at
    /// <paramref name="lookupVersion"/>, by asking its factory for one.
    /// </summary>
    private static Type ResolveSourceType(Assembly originalAssembly, ClassIDType classId, UnityVersion lookupVersion)
    {
        int id = (int)classId;
        string enumName = classId.ToString();
        string baseNamespace = $"AssetRipper.SourceGenerated.Classes.ClassID_{id}";

        Type? factoryType = originalAssembly.GetType($"{baseNamespace}.{enumName}");

        // Some enum members carry a disambiguating "_<id>" suffix the class itself does not.
        if (factoryType == null)
        {
            string suffix = $"_{id}";
            if (enumName.EndsWith(suffix))
            {
                string cleanName = enumName.Substring(0, enumName.Length - suffix.Length);
                factoryType = originalAssembly.GetType($"{baseNamespace}.{cleanName}");
            }
        }

        if (factoryType == null)
            throw new InvalidOperationException($"[RipperHook] Could not find factory type for {classId}");

        var mi = factoryType.GetMethod("Create", new[] { typeof(AssetInfo), typeof(UnityVersion) });
        if (mi == null)
            throw new InvalidOperationException($"[RipperHook] Create method missing on {factoryType.FullName}");

        object instance = mi.Invoke(null, new object[] { null!, lookupVersion })!;
        return instance.GetType();
    }

    // SetAssetListField is AR specific
    protected void SetAssetListField<T>(Type type, string name, ref EndianSpanReader reader, bool isAlign = true) where T : UnityAssetBase, new()
    {
        var field = type.GetField(name, ReflectionExtensions.PrivateInstanceBindFlag());
        if (field == null) return;

        var fieldType = field.FieldType;
        var filedObj = Activator.CreateInstance(fieldType);

        if (isAlign)
            ((AssetList<T>)filedObj).ReadRelease_ArrayAlign_Asset(ref reader);
        else
            ((AssetList<T>)filedObj).ReadRelease_Array_Asset(ref reader);

        field.SetValue(this, filedObj);
    }
}
