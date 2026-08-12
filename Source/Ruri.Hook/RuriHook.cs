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
                    types = tle.Types.Where(t => t != null).ToArray()!;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null) continue;

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

        public static IEnumerable<string> BuildHookIds(GameHookAttribute attribute)
        {
            ArgumentNullException.ThrowIfNull(attribute);
            yield return BuildHookId(attribute);

            foreach (string alsoCoversVersion in attribute.AlsoCoversVersions)
            {
                yield return $"{attribute.GameName}_{alsoCoversVersion}";
            }
        }

        /// <summary>
        /// Raised when a game-specific hook leaves the active set, before the newly
        /// desired hooks are applied. A game hook installs more than MonoMod detours --
        /// it also assigns plain statics (decoders, VFS readers) that tearing the
        /// detours down cannot unset. Subscribers clear those, so switching games in a
        /// live process is equivalent to starting fresh. The kernel deliberately does
        /// not know what any of that state is; whoever owns it registers here.
        /// </summary>
        public static event Action? GameHookRemoved;

        public static void ApplyHooks(HookConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);

            lock (LifecycleSyncRoot)
            {
                List<(Type Type, GameHookAttribute Attribute)> availableHooks = GetAvailableHooks();
                HashSet<string> availableHookIds = new(availableHooks.SelectMany(static hook => BuildHookIds(hook.Attribute)), StringComparer.OrdinalIgnoreCase);

                foreach (string ghostHookId in config.EnabledHooks.Where(id => !availableHookIds.Contains(id)).OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    config.EnabledHooks.Remove(ghostHookId);
                    Console.WriteLine($"[RuriHook] Dropping unknown hook '{ghostHookId}' from config: no matching hook implementation was found.");
                }

                HashSet<string> desiredHookIds = new(config.EnabledHooks, StringComparer.OrdinalIgnoreCase);
                DropExtraGames(config, availableHooks, desiredHookIds);

                HashSet<string> gameHookIds = new(
                    availableHooks.Where(static hook => hook.Attribute.IsGameSpecific)
                        .SelectMany(static hook => BuildHookIds(hook.Attribute)),
                    StringComparer.OrdinalIgnoreCase);
                bool droppedGameHook = false;
                foreach (string hookId in ActiveHookIds.Except(desiredHookIds, StringComparer.OrdinalIgnoreCase).OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    droppedGameHook |= gameHookIds.Contains(hookId);
                    RemoveHookCore(hookId);
                }
                if (droppedGameHook)
                {
                    GameHookRemoved?.Invoke();
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

        private static void DropExtraGames(HookConfig config,
            List<(Type Type, GameHookAttribute Attribute)> availableHooks,
            HashSet<string> desiredHookIds)
        {
            Dictionary<string, string> gameOfId = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, attribute) in availableHooks)
            {
                if (!attribute.IsGameSpecific) continue;
                foreach (string id in BuildHookIds(attribute))
                {
                    gameOfId[id] = attribute.GameName;
                }
            }

            List<string> games = config.EnabledHooks
                .Where(id => desiredHookIds.Contains(id) && gameOfId.ContainsKey(id))
                .ToList();
            if (games.Count <= 1)
            {
                return;
            }

            string keptGame = gameOfId[games[^1]];
            foreach (string hookId in games.Where(id => !string.Equals(gameOfId[id], keptGame, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                desiredHookIds.Remove(hookId);
                config.EnabledHooks.Remove(hookId);
                Console.WriteLine($"[RuriHook] Dropping game hook '{hookId}': only one game can be active at a time, and '{keptGame}' is the one selected.");
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
                List<(Type Type, GameHookAttribute Attribute)> availableHooks = GetAvailableHooks();
                foreach (var (type, attr) in availableHooks)
                {
                    if (BuildHookIds(attr).Any(id => string.Equals(id, hookId, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (attr.IsGameSpecific)
                        {
                            RemoveOtherGames(availableHooks, attr.GameName);
                        }
                        return ApplyHookCore(hookId, type);
                    }
                }

                Console.WriteLine($"[RuriHook] Failed to enable hook {hookId}: no matching hook implementation was found.");
                return false;
            }
        }

        private static void RemoveOtherGames(List<(Type Type, GameHookAttribute Attribute)> availableHooks, string keptGame)
        {
            foreach (var (_, attribute) in availableHooks)
            {
                if (!attribute.IsGameSpecific ||
                    string.Equals(attribute.GameName, keptGame, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (string hookId in BuildHookIds(attribute).Where(ActiveHookIds.Contains).ToArray())
                {
                    Console.WriteLine($"[RuriHook] Switching game: '{hookId}' makes way for '{keptGame}'.");
                    RemoveHookCore(hookId);
                }
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
                Console.WriteLine($"[RuriHook] Failed to enable hook {hookId}: {ex}");
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
