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
    public delegate void ReadReleaseDelegate(object asset, ref EndianSpanReader reader);

    private List<IHookModule> _modules = new();

    protected RipperHookCommon()
    {
    }

    public override void Initialize()
    {
        base.Initialize();        ProcessGameHooks();

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

    protected void ApplyCapabilities(GameType game)
    {
        RipperHookAttribute attr = GetType().GetCustomAttribute<RipperHookAttribute>()
            ?? throw new InvalidOperationException($"{GetType().Name} has no {nameof(RipperHookAttribute)}.");
        CapabilityResolver.Apply(game, attr.Version, Registry);
    }

    protected override void InitAttributeHook()
    {
        base.InitAttributeHook();
    }

    protected void ProcessGameHooks()
    {
        var type = GetType();
        var ripperHookAttr = type.GetCustomAttribute<RipperHookAttribute>();
        if (ripperHookAttr == null) return;

        var hookClassAttrs = type.GetCustomAttributes<TypeTreeHookAttribute>();
        if (!hookClassAttrs.Any())
        {
            return;
        }

        HookLogger.LogRaw($"    Found {hookClassAttrs.Count()} TypeTreeHook attributes in {type.Name}.");

        var classIds = hookClassAttrs.Select(a => a.ClassID).ToList();

        TypeTreeVersion targetVersion = GetTargetVersion(ripperHookAttr);
        if (targetVersion.IsEmpty)
        {
            HookLogger.LogFailure($"[-] {type.Name} declares TypeTreeHook classes but no type tree version; override {nameof(GetTargetVersion)}.");
            return;
        }

        HookClasses(classIds, targetVersion);
    }

    protected virtual TypeTreeVersion GetTargetVersion(RipperHookAttribute attr) => default;

    protected UnityVersion GetEngineVersion()
    {
        RipperHookAttribute attr = GetType().GetCustomAttribute<RipperHookAttribute>()
            ?? throw new InvalidOperationException($"{GetType().Name} has no {nameof(RipperHookAttribute)}.");
        return TypeTreeDatabase.GetEngineVersion(GetTargetVersion(attr));
    }

    protected void HookClasses(
        IEnumerable<ClassIDType> classIds,
        TypeTreeVersion targetVersion,
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

        UnityVersion lookupVersion = TypeTreeDatabase.GetEngineVersion(targetVersion);

        TypeTreeDatabase.ActiveVersion = targetVersion;
        RegisterModule(new HookUtils.TypeTreeFallbackHook.TypeTreeFallbackHook());
        RegisterModule(new AR.EngineAssetsTpkHook());
        RegisterModule(new AR.ForkClassGuidHook());

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
                    HookLogger.LogFailure($"[-] Failed {classId}: no type tree at {targetVersion} in {TypeTreeDatabase.Origin}");
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

    private static Type ResolveSourceType(Assembly originalAssembly, ClassIDType classId, UnityVersion lookupVersion)
    {
        int id = (int)classId;
        string enumName = classId.ToString();
        string baseNamespace = $"AssetRipper.SourceGenerated.Classes.ClassID_{id}";

        Type? factoryType = originalAssembly.GetType($"{baseNamespace}.{enumName}");

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
