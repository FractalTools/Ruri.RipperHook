using System.Collections.Generic;
using System.Reflection;
using Ruri.Hook.Core;
using Ruri.Hook.Utils;
using System;
using Ruri.Hook.Config;
using System.Linq;

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

        /// <summary>
        /// Raised when a game decoder leaves the active set, before the newly desired hooks are
        /// applied. A decoder installs more than MonoMod detours -- it also assigns plain statics
        /// (decoders, VFS readers) that tearing the detours down cannot unset. Subscribers clear
        /// those, so switching games in a live process is equivalent to starting fresh. The kernel
        /// deliberately does not know what any of that state is; whoever owns it registers here.
        /// </summary>
        public static event Action? GameHookRemoved;

        /// <summary>
        /// Make exactly <paramref name="config"/>'s hook set active, enabling and disabling only
        /// the delta -- safe to call as often as a host likes. An id naming nothing is dropped
        /// loudly, and at most one DECODER survives (a process reads one game at a time; two
        /// decoders patch the same methods with different layouts): the last one listed wins,
        /// which is the one a host just selected. Features are applied first and the decoder
        /// last, so the game's own reading of a method sits closest to it.
        /// </summary>
        public static void ApplyHooks(HookConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);

            lock (LifecycleSyncRoot)
            {
                List<string> features = new();
                string? decoder = null;

                foreach (string hookId in config.EnabledHooks.ToArray())
                {
                    if (HookCatalog.FeatureByName(hookId) is not null)
                    {
                        features.Add(hookId);
                        continue;
                    }
                    if (HookCatalog.DecoderById(hookId) is not null)
                    {
                        if (decoder is not null)
                        {
                            config.EnabledHooks.Remove(decoder);
                            Console.WriteLine($"[RuriHook] Dropping decoder '{decoder}': one game is read at a time, and '{hookId}' is the one selected.");
                        }
                        decoder = hookId;
                        continue;
                    }
                    config.EnabledHooks.Remove(hookId);
                    Console.WriteLine($"[RuriHook] Dropping unknown hook '{hookId}' from config: no matching hook implementation was found.");
                }

                features.Sort(StringComparer.OrdinalIgnoreCase);
                List<string> desired = new(features);
                if (decoder is not null)
                {
                    desired.Add(decoder);
                }

                bool droppedDecoder = false;
                foreach (string hookId in ActiveHookIds.Except(desired, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    droppedDecoder |= HookCatalog.DecoderById(hookId) is not null;
                    RemoveHookCore(hookId);
                }
                if (droppedDecoder)
                {
                    GameHookRemoved?.Invoke();
                }

                foreach (string hookId in desired)
                {
                    ApplyHookCore(hookId, HookCatalog.TypeOf(hookId)!);
                }
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
