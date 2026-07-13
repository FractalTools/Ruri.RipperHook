using MonoMod.RuntimeDetour;
using Ruri.Hook.Core;
using Ruri.Hook.Utils;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook
{
    public static class RuriRuntimeHook
    {
        public static List<ILHook> ilHooks = new List<ILHook>();

        private static readonly object LoadedGameHooksSyncRoot = new();
        private static readonly Dictionary<string, HashSet<GameType>> LoadedGameHooks = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsGameLoaded(GameType gameType)
        {
            lock (LoadedGameHooksSyncRoot)
            {
                foreach (HashSet<GameType> gameTypes in LoadedGameHooks.Values)
                {
                    if (gameTypes.Contains(gameType))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static void RegisterLoadedGameHook(GameType gameType)
        {
            if (gameType == GameType.Unknown)
            {
                return;
            }

            string scopeId = HookManager.CurrentScopeId ?? string.Empty;
            bool added;
            lock (LoadedGameHooksSyncRoot)
            {
                if (!LoadedGameHooks.TryGetValue(scopeId, out HashSet<GameType>? gameTypes))
                {
                    gameTypes = new HashSet<GameType>();
                    LoadedGameHooks[scopeId] = gameTypes;
                }

                added = gameTypes.Add(gameType);
            }

            if (added)
            {
                HookManager.RegisterCleanup(() => RemoveLoadedGameHook(scopeId, gameType));
            }
        }

        public static void DisposeAll()
        {
            // Dispose hooks tracked by Core
            HookManager.DisposeAll();
            global::Ruri.Hook.RuriHook.ClearAppliedHooks();
            ReflectionExtensions.ClearAppliedHookGuards();
            HookDispatcher.Clear();
            lock (LoadedGameHooksSyncRoot)
            {
                LoadedGameHooks.Clear();
            }

            // Dispose hooks tracked locally (if any)
            foreach (var hook in ilHooks)
            {
                hook.Dispose();
            }
            ilHooks.Clear();
        }

        private static void RemoveLoadedGameHook(string scopeId, GameType gameType)
        {
            lock (LoadedGameHooksSyncRoot)
            {
                if (!LoadedGameHooks.TryGetValue(scopeId, out HashSet<GameType>? gameTypes))
                {
                    return;
                }

                gameTypes.Remove(gameType);
                if (gameTypes.Count == 0)
                {
                    LoadedGameHooks.Remove(scopeId);
                }
            }
        }
    }
}
