using System;
using System.Collections.Generic;
using System.IO;
using Ruri.Hook;
using Ruri.Hook.Config;
using Ruri.Hook.Core;
using Ruri.Hook.UI;

namespace Ruri.FModelHook
{
    public static class Program
    {
        // Single source of truth for FModelHook's hook-selection JSON,
        // mirroring Ruri.RipperHook's `RuriRipperHook.json` convention.
        private const string ConfigFileName = "RuriFModelHook.json";

        [STAThread]
        public static void Main(string[] args)
        {
            HookConfig config = ResolveHookConfig(args);
            RuriHook.ApplyHooks(config);
            LaunchFModel();
        }

        // Same selection model as Ruri.RipperHook.Program: explicit
        // `--hook <id>` arguments take precedence; otherwise load the
        // persisted config; if no hooks are persisted yet, prompt the
        // user via the shared WinForms HookSelectionForm.
        // Hook IDs are `{GameName}_{Version}` per FModelHookAttribute.
        private static HookConfig ResolveHookConfig(string[] args)
        {
            var hookIds = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--hook", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    hookIds.Add(args[++i]);
                }
            }

            if (hookIds.Count > 0)
            {
                var explicitConfig = new HookConfig();
                foreach (string id in hookIds)
                {
                    explicitConfig.EnabledHooks.Add(id);
                }
                HookLogger.Log($"[Ruri.FModelHook] CLI mode: hooks={string.Join(", ", hookIds)}");
                return explicitConfig;
            }

            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
            HookConfig config = HookConfig.Load(configPath);

            if (config.EnabledHooks.Count == 0)
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                System.Windows.Forms.Application.Run(new HookSelectionForm(config, configPath));
                config = HookConfig.Load(configPath);
            }

            HookLogger.Log($"[Ruri.FModelHook] Persistent config: {config.EnabledHooks.Count} hooks enabled ({string.Join(", ", config.EnabledHooks)})");
            return config;
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
