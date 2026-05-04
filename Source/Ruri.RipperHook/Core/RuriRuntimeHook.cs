using System.Reflection;
using MonoMod.RuntimeDetour;
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
        private static readonly HashSet<GameType> LoadedGameHooks = new();

        public static bool IsGameLoaded(GameType gameType)
        {
            return LoadedGameHooks.Contains(gameType);
        }

        public static void RegisterLoadedGameHook(GameType gameType)
        {
            if (gameType == GameType.Unknown)
            {
                return;
            }

            LoadedGameHooks.Add(gameType);
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
            HookDispatcher.Clear();
            LoadedGameHooks.Clear();
            gameVer = "";
            gameName = "";

            // Dispose hooks tracked locally (if any)
            foreach (var hook in ilHooks)
            {
                hook.Dispose();
            }
            ilHooks.Clear();
        }
    }
}
