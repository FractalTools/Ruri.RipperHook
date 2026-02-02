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
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        var attr = type.GetCustomAttribute<GameHookAttribute>();
                        if (attr != null)
                        {
                            hooks.Add((type, attr));
                        }
                    }
                }
                catch
                {
                    // Ignore assemblies that can't be inspected
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
