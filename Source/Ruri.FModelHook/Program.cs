using System;
using System.Linq;
using System.Reflection;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;

namespace Ruri.FModelHook
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            InitializeHooks();
            LaunchFModel();
        }

        private static void InitializeHooks()
        {
            HookLogger.Log("Scanning for hooks...");

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var hookTypes = assembly.GetTypes()
                    .Where(t => t.GetCustomAttribute<GameHookAttribute>() != null)
                    .ToList();

                HookLogger.Log($"Found {hookTypes.Count} hooks.");

                foreach (var hookType in hookTypes)
                {
                    var attr = hookType.GetCustomAttribute<GameHookAttribute>();
                    var hookName = attr?.GameName ?? hookType.Name;

                    try
                    {
                        var instance = (RuriHook)Activator.CreateInstance(hookType, true)!;
                        instance.Initialize();
                        HookLogger.LogSuccess($"[+] Hook '{hookName}' initialized successfully.");
                    }
                    catch (Exception ex)
                    {
                        HookLogger.LogFailure($"[-] Failed to initialize hook '{hookName}': {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"Critical error during hook initialization: {ex}");
            }
        }

        private static void LaunchFModel()
        {
            HookLogger.Log("Launching FModel...");
            try
            {
                var app = new FModel.App();
                app.InitializeComponent();
                app.Run();
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"FModel crashed: {ex}");
            }
        }
    }
}
