using System;
using System.Collections.Generic;
using System.IO;
using Ruri.Hook;
using Ruri.Hook.Config;
using Ruri.Hook.Core;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.GUI;

public static class Program
{
    private const string ConfigFileName = "RuriFModelHook.json";

    [STAThread]
    public static void Main(string[] args)
    {
        EnsureHookAssembliesLoaded();

        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);

        HookConfig config = HookConfig.Load(configPath);
        WireModuleSettings(config, configPath);

        new HookMenuBootstrap().Initialize();

        new ConsoleLogSinkHook().Initialize();

        ApplyEnabledHooks(config, configPath, args);
        LaunchFModel();
    }

    private static void ApplyEnabledHooks(HookConfig config, string configPath, string[] args)
    {
        var cliHookIds = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--hook", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                cliHookIds.Add(args[++i]);
            }
        }

        if (cliHookIds.Count > 0)
        {
            HookConfig explicitConfig = new();
            foreach (string id in cliHookIds)
            {
                explicitConfig.EnabledHooks.Add(id);
            }
            HookLogger.Log($"[Ruri.FModelHook] CLI mode: hooks={string.Join(", ", cliHookIds)}");
            RuriHook.ApplyHooks(explicitConfig);
            return;
        }

        if (config.EnabledHooks.Count == 0)
        {
            foreach (var decoder in Ruri.Hook.Core.HookCatalog.Decoders)
            {
                config.EnabledHooks.Add(decoder.Id);
            }
            config.Save(configPath);
            HookLogger.Log($"[Ruri.FModelHook] No persisted config — auto-enabled {config.EnabledHooks.Count} hooks. Toggle via Hooks menu in FModel.");
        }

        HookLogger.Log($"[Ruri.FModelHook] Persistent config: {config.EnabledHooks.Count} hooks enabled ({string.Join(", ", config.EnabledHooks)})");
        RuriHook.ApplyHooks(config);
    }

    private static void WireModuleSettings(HookConfig config, string configPath)
    {
        ShaderDecompilerSettings shader = config.GetModuleSettings<ShaderDecompilerSettings>(ShaderDecompilerSettings.ModuleKey) ?? new ShaderDecompilerSettings();
        ShaderDecompilerSettingsAccess.Replace(shader);
        ShaderDecompilerSettingsAccess.RegisterSaver(updated =>
        {
            HookConfig live = HookConfig.Load(configPath);
            live.SetModuleSettings(ShaderDecompilerSettings.ModuleKey, updated);
            live.Save(configPath);
        });
    }

    private static void EnsureHookAssembliesLoaded()
    {
        _ = typeof(Ruri.FModelHook.GameType);
        _ = typeof(Ruri.FModelHook.ShaderDecompiler.UE_ShaderDecompiler_Hook);

        Ruri.Hook.Core.HookCatalog.DeclareHost(typeof(Ruri.FModelHook.Attributes.FModelHookAttribute));
        TryLoad("Ruri.FModelHook");

        int hookCount = Ruri.Hook.Core.HookCatalog.Decoders.Count;
        HookLogger.Log($"[Ruri.FModelHook.GUI] Hook assemblies loaded — discovered {hookCount} [GameHookAttribute] type(s).");
        if (hookCount == 0)
        {
            HookLogger.LogFailure("[Ruri.FModelHook.GUI] No hooks discovered. Check that Ruri.FModelHook.dll is next to the executable.");
        }
    }

    private static void TryLoad(string assemblyName)
    {
        try
        {
            System.Reflection.Assembly.Load(assemblyName);
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[Ruri.FModelHook.GUI] Assembly.Load(\"{assemblyName}\") failed: {ex.Message}");
        }
    }

    private static void LaunchFModel()
    {
        HookLogger.Log("Launching FModel...");
        try
        {
            FModel.App app = new();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"FModel crashed: {ex}");
        }
    }
}
