using System.Collections.Generic;
using System.Reflection;
using Ruri.Hook.Core;
using Ruri.Hook.Utils;
using System;
using Ruri.Hook.Config;
using System.Linq;
using Ruri.Hook.Attributes;

namespace Ruri.Hook
{
    public abstract class RuriHook
    {
        protected readonly HookRegistry Registry = new();
        protected List<MethodInfo> methodHooks = new();
        
        public virtual void Initialize()
        {
            InitAttributeHook();
        }

        protected virtual void InitAttributeHook()
        {
            Registry.ApplyTypeHooks(GetType());
            
            if (methodHooks.Count > 0)
            {
                 Registry.ApplyManualHooks(methodHooks);
            }
        }

        protected void AddMethodHook(Type type, string name)
        {
            var method = type.GetMethod(name, ReflectionExtensions.AnyBindFlag());
            if (method != null)
            {
                methodHooks.Add(method);
            }
        }

        protected void SetPrivateField(Type type, string name, object newValue)
        {
            type.GetField(name, ReflectionExtensions.PrivateInstanceBindFlag())?.SetValue(this, newValue);
        }

        protected object? GetPrivateField(Type type, string name)
        {
            return type.GetField(name, ReflectionExtensions.PrivateInstanceBindFlag())?.GetValue(this);
        }

        public static List<(Type Type, GameHookAttribute Attribute)> GetAvailableHooks()
        {
            var hooks = new List<(Type Type, GameHookAttribute Attribute)>();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                // Skip framework / system assemblies up front. They never
                // carry our hooks and walking them is the bulk of the cost
                // (and the most likely source of GetTypes / GetAttributes
                // failures). Match on AssemblyName so framework facades and
                // dynamically-loaded ones are covered.
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
                    // GetTypes() on assemblies with unresolved transitive
                    // deps throws but exposes the partial list. Use what
                    // we got — the old behaviour discarded the whole DLL.
                    types = tle.Types.Where(t => t != null).ToArray()!;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null) continue;

                    // Use the non-generic GetCustomAttributes(inherit:false)
                    // and a runtime `is` cast instead of GetCustomAttribute<T>.
                    // The generic form has been known to miss derived
                    // attribute classes when the requested base lives in a
                    // different assembly under certain trim / load-context
                    // configurations; the runtime cast is bulletproof.
                    object[] attrs;
                    try
                    {
                        attrs = type.GetCustomAttributes(inherit: false);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (object a in attrs)
                    {
                        if (a is GameHookAttribute gha)
                        {
                            hooks.Add((type, gha));
                            break;
                        }
                    }
                }
            }

            return hooks.OrderBy(x => x.Attribute.GameName).ThenBy(x => x.Attribute.Version).ToList();
        }

        public static void ApplyHooks(HookConfig config)
        {
            var enabledHooks = config.EnabledHooks;
            var availableHooks = GetAvailableHooks();

            foreach (var (type, attr) in availableHooks)
            {
                var id = $"{attr.GameName}_{attr.Version}";
                if (enabledHooks.Contains(id))
                {
                    try
                    {
                        if (Activator.CreateInstance(type, true) is RuriHook hook)
                        {
                            Console.WriteLine();
                            Console.WriteLine($"[RuriHook] Enabled hook: {id}");
                            hook.Initialize();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RuriHook] Failed to enable hook {id}: {ex.Message}");
                    }
                }
            }
        }
    }
}
