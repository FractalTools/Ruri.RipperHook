using System;
using System.Collections.Generic;
using System.IO;
using Ruri.Hook;
using Ruri.Hook.Config;
using Ruri.Hook.Core;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.GUI;

// Entry point for the FModelHook host. This GUI assembly owns Main()
// because Ruri.FModelHook itself is a class library (it carries the
// hooks but doesn't ship Main). Bootstrap order:
//   1. Load the unified host config (RuriFModelHook.json).
//   2. Wire module settings into the typed accessors so any hook ctor
//      sees the persisted values on first read.
//   3. Install the always-on UI detour (MainWindow.OnLoaded ->
//      "Hooks" menu injection). Not gated by EnabledHooks — running
//      this host means you want the menu.
//   4. Apply the user's enabled hooks via Ruri.Hook's discovery flow
//      (walks all loaded assemblies for [FModelHook]-attributed types,
//      finds those listed in config, calls Initialize()).
//   5. Launch FModel.
public static class Program
{
    private const string ConfigFileName = "RuriFModelHook.json";

    [STAThread]
    public static void Main(string[] args)
    {
        // Force-load every hook-carrying assembly BEFORE hook discovery
        // runs. Without this, `RuriHook.GetAvailableHooks()` only sees
        // already-loaded assemblies and the EnabledHooks dialog comes
        // up empty.
        //
        // We use BOTH approaches because either one alone has failed in
        // practice:
        //   * `typeof(...)` forces metadata load but the runtime can
        //     skip pulling in the rest of the DLL's types under some
        //     configs (trim modes, multi-load-context).
        //   * `Assembly.Load("Ruri.FModelHook")` explicitly resolves
        //     the assembly through the runtime's resolver. After this
        //     call the assembly is in the AppDomain regardless of what
        //     the JIT decided about typeof.
        EnsureHookAssembliesLoaded();

        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);

        HookConfig config = HookConfig.Load(configPath);
        WireModuleSettings(config, configPath);

        // Always-on UI infrastructure: detours MainWindow.OnLoaded to
        // inject the "Hooks" top-level menu. Initialize() installs the
        // [RetargetMethod] detour declared on HookMenuBootstrap.
        new HookMenuBootstrap().Initialize();

        ApplyEnabledHooks(config, configPath, args);
        LaunchFModel();
    }

    // Hook-selection resolution:
    //   * `--hook <id>` CLI args take precedence (one per id, repeatable).
    //   * Otherwise consume the persisted `config.EnabledHooks`.
    //   * If still empty, default to enabling EVERY discovered hook
    //     (lets the user start exploring on first launch; they can
    //     selectively disable specific hooks via Hooks > Enabled
    //     Hooks... and restart).
    //
    // Hook IDs are `{GameName}_{Version}` per FModelHookAttribute.
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
            foreach (var (_, attr) in RuriHook.GetAvailableHooks())
            {
                config.EnabledHooks.Add($"{attr.GameName}_{attr.Version}");
            }
            config.Save(configPath);
            HookLogger.Log($"[Ruri.FModelHook] No persisted config — auto-enabled {config.EnabledHooks.Count} hooks. Toggle via Hooks menu in FModel.");
        }

        HookLogger.Log($"[Ruri.FModelHook] Persistent config: {config.EnabledHooks.Count} hooks enabled ({string.Join(", ", config.EnabledHooks)})");
        RuriHook.ApplyHooks(config);
    }

    // Pulls the ShaderDecompiler module settings out of the unified
    // config and registers a saver that writes back to the same file.
    // The saver re-reads + re-writes so a future settings UI for
    // another module doesn't get clobbered by a save of the shader
    // settings.
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

    // Touches a type from each hook-carrying assembly + asks the runtime
    // resolver to load by name. Logs the discovered hook count so users
    // can spot a misconfigured publish layout (count == 0 means the DLL
    // didn't make it next to the Exe, or the AssemblyName is wrong).
    private static void EnsureHookAssembliesLoaded()
    {
        // Touching the type forces metadata + assembly load.
        _ = typeof(Ruri.FModelHook.GameType);
        _ = typeof(Ruri.FModelHook.Game.SBUE.ShaderDecompiler.UE_ShaderDecompiler_Hook);
        _ = typeof(Ruri.FModelHook.Game.SBUE.AutoExport.UE_ShaderDecompiler_AutoExport_Hook);

        // Belt-and-braces: explicit Assembly.Load by name. No-op if
        // already loaded; surfaces a logged failure if the DLL is
        // missing from the runtime probe path.
        TryLoad("Ruri.FModelHook");

        int hookCount = RuriHook.GetAvailableHooks().Count;
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
