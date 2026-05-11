using System.Reflection;
using MonoMod.RuntimeDetour;
using Ruri.Hook.Core;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.Core;
namespace Ruri.RipperHook
{
    public static class RuriRuntimeHook
    {
        public static List<ILHook> ilHooks = new List<ILHook>();

        // Missing fields referenced in errors
        public static string gameVer = "";
        public static string gameName = "";
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

        public static void Init()
        {
            HookLogger.Log($"Initializing hook: {gameName}");

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Type? hookClass = null;

            foreach (var asm in assemblies)
            {
                try
                {
                    var types = asm.GetTypes();
                    hookClass = types.FirstOrDefault(t =>
                    {
                        var attr = t.GetCustomAttribute<RipperHookAttribute>();
                        if (attr == null) return false;

                        return MatchesHookName(attr, gameName);
                    });
                    if (hookClass != null) break;
                }
                catch { }
            }

            if (hookClass != null)
            {
                // Instantiate and Initialize
                var instance = (RipperHookCommon)Activator.CreateInstance(hookClass, true);
                instance.Initialize();
                HookLogger.LogSuccess($"Hook {gameName} initialized successfully.");
            }
            else
            {
                HookLogger.LogWarning($"No implementation found for hook: {gameName}");
            }
        }

        private static bool MatchesHookName(RipperHookAttribute attr, string hookName)
        {
            if (attr.GameType.ToString() == hookName)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(attr.Version))
            {
                var constructedName = $"{attr.GameType}_{attr.Version}".Replace(".", "_");
                var targetName = hookName.Replace(".", "_");
                return constructedName == targetName;
            }

            return false;
        }

        public static void DisposeAll()
        {
            // Dispose hooks tracked by Core
            HookManager.DisposeAll();
            global::Ruri.Hook.RuriHook.ClearAppliedHooks();
            HookDispatcher.Clear();
            lock (LoadedGameHooksSyncRoot)
            {
                LoadedGameHooks.Clear();
            }
            gameVer = "";
            gameName = "";

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
