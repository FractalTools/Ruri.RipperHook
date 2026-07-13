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
        private static readonly object LifecycleSyncRoot = new();
        private static readonly HashSet<string> ActiveHookIds = new(StringComparer.OrdinalIgnoreCase);
        
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

            List<(Type Type, GameHookAttribute Attribute)> ordered = hooks.OrderBy(x => x.Attribute.GameName).ThenBy(x => x.Attribute.Version).ToList();
            ValidateNoIdCollisions(ordered);
            return ordered;
        }

        // A hook id must resolve to exactly one implementation. Without this, two classes (or one
        // class's declared version colliding with another's alias) could silently register the same
        // selectable id, and whichever happened to be enumerated last would win with no diagnostic --
        // the same class of "nobody would notice" mistake the duplicate-hook guard in
        // ReflectionExtensions exists to catch, one layer up.
        private static void ValidateNoIdCollisions(List<(Type Type, GameHookAttribute Attribute)> hooks)
        {
            var collisions = hooks
                .SelectMany(hook => BuildHookIds(hook.Attribute).Select(id => (Id: id, hook.Type)))
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(x => x.Type).Distinct().Count() > 1);

            foreach (var collision in collisions)
            {
                string offenders = string.Join(", ", collision.Select(x => x.Type.FullName).Distinct());
                throw new InvalidOperationException($"[RuriHook] hook id '{collision.Key}' is claimed by more than one implementation: {offenders}. A hook id must resolve to exactly one class.");
            }
        }

        public static string BuildHookId(GameHookAttribute attribute)
        {
            ArgumentNullException.ThrowIfNull(attribute);
            return $"{attribute.GameName}_{attribute.Version}";
        }

        /// <summary>
        /// Every id <paramref name="attribute"/> answers to: its primary <see cref="BuildHookId"/>
        /// plus one more per entry in <see cref="GameHookAttribute.AlsoCoversVersions"/> -- so a
        /// version whose resolved behavior is identical to another's shows up as its own listed,
        /// selectable hook without needing its own class.
        /// </summary>
        public static IEnumerable<string> BuildHookIds(GameHookAttribute attribute)
        {
            ArgumentNullException.ThrowIfNull(attribute);
            yield return BuildHookId(attribute);

            foreach (string alsoCoversVersion in attribute.AlsoCoversVersions)
            {
                yield return $"{attribute.GameName}_{alsoCoversVersion}";
            }
        }

        public static void ApplyHooks(HookConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);

            lock (LifecycleSyncRoot)
            {
                List<(Type Type, GameHookAttribute Attribute)> availableHooks = GetAvailableHooks();
                HashSet<string> availableHookIds = new(availableHooks.SelectMany(static hook => BuildHookIds(hook.Attribute)), StringComparer.OrdinalIgnoreCase);

                // Self-heal the persisted config: drop any enabled-hook id that has no
                // implementation in this build (a hook that was renamed or deleted, e.g. an
                // export-mode hook folded into native/GUI code). Without this a ghost id
                // survives every config round-trip, spams "no matching implementation" on
                // every launch, and — worse — poisons the GUI's temporary-hook restore path:
                // a filtered export captures the pre-export config, layers feature hooks on
                // top, then restores the captured config afterwards; a ghost id in that
                // captured set can never re-enable, so the restore looks like it silently
                // tore every hook down. Pruning here (the single choke point every host and
                // every reconfiguration flows through) keeps the id set honest everywhere.
                foreach (string ghostHookId in config.EnabledHooks.Where(id => !availableHookIds.Contains(id)).OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    config.EnabledHooks.Remove(ghostHookId);
                    Console.WriteLine($"[RuriHook] Dropping unknown hook '{ghostHookId}' from config: no matching hook implementation was found.");
                }

                HashSet<string> desiredHookIds = new(config.EnabledHooks, StringComparer.OrdinalIgnoreCase);

                foreach (string hookId in ActiveHookIds.Except(desiredHookIds, StringComparer.OrdinalIgnoreCase).OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    RemoveHookCore(hookId);
                }

                foreach (var (type, attr) in availableHooks)
                {
                    foreach (string hookId in BuildHookIds(attr))
                    {
                        if (desiredHookIds.Contains(hookId))
                        {
                            ApplyHookCore(hookId, type);
                        }
                    }
                }
            }
        }

        public static bool ApplyHook(string hookId)
        {
            if (string.IsNullOrWhiteSpace(hookId))
            {
                return false;
            }

            lock (LifecycleSyncRoot)
            {
                foreach (var (type, attr) in GetAvailableHooks())
                {
                    if (BuildHookIds(attr).Any(id => string.Equals(id, hookId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return ApplyHookCore(hookId, type);
                    }
                }

                Console.WriteLine($"[RuriHook] Failed to enable hook {hookId}: no matching hook implementation was found.");
                return false;
            }
        }

        public static bool RemoveHook(string hookId)
        {
            if (string.IsNullOrWhiteSpace(hookId))
            {
                return false;
            }

            lock (LifecycleSyncRoot)
            {
                return RemoveHookCore(hookId);
            }
        }

        public static void ClearAppliedHooks()
        {
            lock (LifecycleSyncRoot)
            {
                ActiveHookIds.Clear();
            }
        }

        private static bool ApplyHookCore(string hookId, Type type)
        {
            if (ActiveHookIds.Contains(hookId))
            {
                return false;
            }

            try
            {
                HookManager.RunInScope(hookId, () =>
                {
                    if (Activator.CreateInstance(type, true) is not RuriHook hook)
                    {
                        throw new InvalidOperationException($"Type {type.FullName} is not a valid hook implementation.");
                    }

                    Console.WriteLine();
                    Console.WriteLine($"[RuriHook] Enabled hook: {hookId}");
                    hook.Initialize();
                });

                ActiveHookIds.Add(hookId);
                return true;
            }
            catch (Exception ex)
            {
                HookManager.DisposeScope(hookId);
                Console.WriteLine($"[RuriHook] Failed to enable hook {hookId}: {ex.Message}");
                return false;
            }
        }

        private static bool RemoveHookCore(string hookId)
        {
            if (!ActiveHookIds.Remove(hookId))
            {
                return false;
            }

            HookManager.DisposeScope(hookId);
            Console.WriteLine($"[RuriHook] Disabled hook: {hookId}");
            return true;
        }
    }
}
