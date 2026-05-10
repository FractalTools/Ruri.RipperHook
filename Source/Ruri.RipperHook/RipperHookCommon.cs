using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Endian;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using Ruri.Hook;
using Ruri.RipperHook.Core;

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

    protected override void InitAttributeHook()
    {
        base.InitAttributeHook();
        // Custom RipperHook logic can go here if needed
    }

    /// <summary>
    /// Scans for [HookObjectClass] attributes on the current class and registers them.
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

        // Let's assume GeneratedNamespace is standard unless overridden
        string generatedNamespace = "Ruri.SourceGenerated";
        
        // Check if any attribute overrides namespace
        var firstNamespaceOverride = hookClassAttrs.FirstOrDefault(a => a.GeneratedAssemblyNamespace != null);
        if (firstNamespaceOverride != null) generatedNamespace = firstNamespaceOverride.GeneratedAssemblyNamespace!;

        HookClasses(classIds, ripperHookAttr.BaseEngineVersion, targetVersionVec, generatedNamespace);
    }

    protected virtual UnityVersion GetTargetVersion(RipperHookAttribute attr)
    {
        return UnityVersion.Parse(attr.BaseEngineVersion);
    }

    protected void HookClasses(
        IEnumerable<ClassIDType> classIds,
        string sourceUnityVersion,
        UnityVersion targetVersion,
        string generatedAssemblyNamespace = "Ruri.SourceGenerated",
        Dictionary<ClassIDType, ReadReleaseDelegate>? customCallbacks = null)
    {
        Dictionary<ClassIDType, HookDispatcher.ReadReleaseDelegate>? coreCallbacks = null;
        if (customCallbacks != null)
        {
            coreCallbacks = new Dictionary<ClassIDType, HookDispatcher.ReadReleaseDelegate>();
            foreach(var kvp in customCallbacks)
            {
                coreCallbacks[kvp.Key] = (obj, ref reader) => kvp.Value(obj, ref reader);
            }
        }

        Assembly? ruriAssembly = null;
        try
        {
            ruriAssembly = ResolveGeneratedAssembly(generatedAssemblyNamespace);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RipperHook] Warning: Could not resolve assembly {generatedAssemblyNamespace}: {ex.Message}");
        }

        if (ruriAssembly != null)
        {
            UnityVersion lookupVersion = UnityVersion.Parse(sourceUnityVersion);
            
            var universalDestMethod = typeof(HookDispatcher).GetMethod(nameof(HookDispatcher.Universal_ReadRelease), BindingFlags.Public | BindingFlags.Static);
            if (universalDestMethod == null) throw new Exception("Universal_ReadRelease missing");

            var originalAssembly = typeof(ClassIDType).Assembly; 

            foreach(var classId in classIds)
            {
                try 
                {
                   // 1. Resolve AssetRipper Source Type
                   int id = (int)classId;
                   string enumName = classId.ToString();
                   string baseNamespace = $"AssetRipper.SourceGenerated.Classes.ClassID_{id}";
                   
                   // Try standard name
                   string factoryTypeName = $"{baseNamespace}.{enumName}";
                   Type? factoryType = originalAssembly.GetType(factoryTypeName);

                   // Try removing suffix if not found
                   if (factoryType == null)
                   {
                       string suffix = $"_{id}";
                       if (enumName.EndsWith(suffix))
                       {
                           string cleanName = enumName.Substring(0, enumName.Length - suffix.Length);
                           string cleanTypeName = $"{baseNamespace}.{cleanName}";
                           factoryType = originalAssembly.GetType(cleanTypeName);
                       }
                   }

                   if (factoryType == null)
                       throw new InvalidOperationException($"[RipperHook] Could not find factory type for {classId}");

                   var mi = factoryType.GetMethod("Create", new[] { typeof(AssetInfo), typeof(UnityVersion) });
                   if (mi == null)
                       throw new InvalidOperationException($"[RipperHook] Create method missing on {factoryType.FullName}");

                   // Invoke Create(null, lookupVersion) to get an instance, then get its type.
                   object instance = mi.Invoke(null, new object[] { null, lookupVersion });
                   Type sourceType = instance.GetType();
                   string sourceTypeName = sourceType.FullName!;

                   // 2. Resolve Ruri Target Type & Hooks
                   string ruriBaseNamespace = $"{generatedAssemblyNamespace}.Classes.ClassID_{id}";
                   string ruriTypeName = $"{ruriBaseNamespace}.{enumName}";
                   
                   Type? ruriType = ruriAssembly.GetType(ruriTypeName);
                   if (ruriType == null && enumName.EndsWith($"_{id}"))
                   {
                        string cleanName = enumName.Substring(0, enumName.Length - $"_{id}".Length);
                        ruriType = ruriAssembly.GetType($"{ruriBaseNamespace}.{cleanName}");
                   }

                   HookDispatcher.ReadReleaseDelegate? callback = null;
                   if (coreCallbacks != null && coreCallbacks.TryGetValue(classId, out var customAction))
                   {
                       callback = customAction;
                   }

                   if (ruriType == null && callback == null)
                   {
                       continue; 
                   }

                   MethodInfo? createMethod = ruriType?.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(AssetInfo), typeof(UnityVersion) }, null);
                   if (ruriType != null && createMethod == null)
                   {
                        HookLogger.LogFailure($"[-] Failed {classId}: Missing 'Create' method on {ruriType.Name}");
                        continue;
                   }

                   if (createMethod == null && callback == null) 
                   {
                       HookLogger.LogFailure($"[-] Failed {classId}: No callback or Create method");
                       continue;
                   }

                   HookDispatcher.Register(sourceType, createMethod, targetVersion, callback);
                   
                   var readReleaseMethod = sourceType.GetMethod("ReadRelease", BindingFlags.Public | BindingFlags.Instance);
                   if (readReleaseMethod != null)
                   {
                       ReflectionExtensions.RetargetCall(readReleaseMethod, universalDestMethod, 1, true, true);
                       
                       string targetName = "Unknown";
                       if (createMethod != null)
                       {
                           object targetInstance = createMethod.Invoke(null, new object[] { null, targetVersion });
                           targetName = targetInstance.GetType().Name;
                       }

                       HookLogger.LogSuccessRaw($"    [+] Hooked {sourceType.Name} -> {targetName}");
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
    }

    private static Assembly? ResolveGeneratedAssembly(string generatedAssemblyNamespace)
    {
        Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();

        // First prefer already loaded assemblies in the current process.
        Assembly? assembly = loadedAssemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, generatedAssemblyNamespace, StringComparison.Ordinal));
        if (assembly != null)
        {
            return assembly;
        }

        // The common path is the default Ruri.SourceGenerated namespace. Use a direct type anchor so
        // we do not depend on probing rules or display-name based Assembly.Load.
        if (string.Equals(generatedAssemblyNamespace, "Ruri.SourceGenerated", StringComparison.Ordinal))
        {
            return typeof(SourceTpk).Assembly;
        }

        // Namespace overrides may still already be loaded under a matching assembly name.
        return loadedAssemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, generatedAssemblyNamespace, StringComparison.OrdinalIgnoreCase));
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
