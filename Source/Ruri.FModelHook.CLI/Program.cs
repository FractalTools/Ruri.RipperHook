using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using Ruri.Hook;
using Ruri.Hook.Config;
using Ruri.Hook.Core;
using Ruri.ShaderTools;

namespace Ruri.FModelHook.CLI;

// Headless console entry. The CLI essentially forces the same
// hook-driven auto-export flow that Ruri.FModelHook.GUI exposes, but:
//   * No Hooks menu, no settings dialog (stripped from the bootstrap).
//   * FModel's MainWindow is hidden by default (configurable via
//     --show-window).
//   * Auto-export is *always on* — the user invoked the CLI, that IS the
//     intent, no need for a separate `--auto-export-cook` flag.
//
// All native dependencies (CUE4Parse-Natives, Oodle, dxbc2dxil &
// friends) live in the shared FModel bin output folder which this CLI
// also publishes into; running from there is the supported invocation.
public static class Program
{
    private const string ConfigFileName = "RuriFModelHook.json";

    [STAThread]
    public static int Main(string[] args)
    {
        CliOptions opts = CliOptions.Parse(args);
        if (opts.Help)
        {
            Console.WriteLine(CliOptions.HelpText());
            return 0;
        }

        // Force-load the hook-carrying assembly so RuriHook.GetAvailableHooks()
        // sees every hook even before the user runs --list-hooks. The GUI does
        // the same dance via typeof() pinning + Assembly.Load fallback; we
        // mirror it so the CLI behaviour matches.
        EnsureHookAssembliesLoaded();

        if (opts.ListHooks)
        {
            return RunListHooks();
        }

        // Persisted config drives every other module setting (e.g. shader
        // decompiler split-variants); CLI flags can only ADD to the enabled
        // hook set, not subtract from it (matches the GUI flow).
        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
        HookConfig config = HookConfig.Load(configPath);
        WireModuleSettings(config, configPath, opts);

        ApplyEnabledHooks(config, opts);

        // The auto-export hook reads its toggles from Environment.GetCommandLineArgs(),
        // not from this CliOptions bag — that hook is shared with the GUI which
        // exposes the same flags via the same arg set. Synthesise the args the
        // hook expects and append them to the process command line via
        // Environment so the hook sees them when its Initialize() fires.
        InjectHookArgs(opts);

        try
        {
            LaunchFModel(opts);
            return 0;
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[Ruri.FModelHook.CLI] FModel crashed: {ex}");
            return 1;
        }
    }

    private static int RunListHooks()
    {
        var hooks = RuriHook.GetAvailableHooks();
        if (hooks.Count == 0)
        {
            Console.WriteLine("(no hooks discovered)");
            return 1;
        }
        foreach (var (type, attr) in hooks)
        {
            Console.WriteLine($"{attr.GameName}_{attr.Version,-12} [{type.Name}]");
        }
        return 0;
    }

    private static void ApplyEnabledHooks(HookConfig config, CliOptions opts)
    {
        if (opts.Hooks.Count > 0)
        {
            HookConfig explicitConfig = new();
            foreach (string id in opts.Hooks)
            {
                explicitConfig.EnabledHooks.Add(id);
            }
            HookLogger.Log($"[Ruri.FModelHook.CLI] CLI hooks: {string.Join(", ", opts.Hooks)}");
            RuriHook.ApplyHooks(explicitConfig);
            return;
        }

        if (config.EnabledHooks.Count == 0)
        {
            // Match the GUI's first-run behaviour: auto-enable everything so
            // a fresh user gets a working CLI without needing to touch
            // RuriFModelHook.json by hand.
            foreach (var (_, attr) in RuriHook.GetAvailableHooks())
            {
                config.EnabledHooks.Add($"{attr.GameName}_{attr.Version}");
            }
            HookLogger.Log($"[Ruri.FModelHook.CLI] No persisted config — auto-enabled {config.EnabledHooks.Count} hooks.");
        }
        else
        {
            HookLogger.Log($"[Ruri.FModelHook.CLI] Persisted hooks: {string.Join(", ", config.EnabledHooks)}");
        }
        RuriHook.ApplyHooks(config);
    }

    private static void WireModuleSettings(HookConfig config, string configPath, CliOptions opts)
    {
        ShaderDecompilerSettings shader = config.GetModuleSettings<ShaderDecompilerSettings>(ShaderDecompilerSettings.ModuleKey) ?? new ShaderDecompilerSettings();
        if (opts.SplitVariants is bool sv && shader.SplitVariantsToHlslFiles != sv)
        {
            shader = new ShaderDecompilerSettings
            {
                SplitVariantsToHlslFiles = sv,
                WarnIfNoMappings = shader.WarnIfNoMappings,
            };
        }
        ShaderDecompilerSettingsAccess.Replace(shader);
        ShaderDecompilerSettingsAccess.RegisterSaver(updated =>
        {
            HookConfig live = HookConfig.Load(configPath);
            live.SetModuleSettings(ShaderDecompilerSettings.ModuleKey, updated);
            live.Save(configPath);
        });
    }

    // The auto-export hook (Ruri.FModelHook.Game.SBUE.AutoExport.UE_ShaderDecompiler_AutoExport_Hook)
    // reads its flags from the process command line via Environment.GetCommandLineArgs().
    // Synthesise the args it expects so a CLI invocation behaves like a
    // GUI invocation that opted into auto-export.
    //
    // We append rather than replace so the user's actual args are still
    // visible (--show-window etc. are CLI-only and the hook ignores them).
    private static void InjectHookArgs(CliOptions opts)
    {
        var injected = new List<string> { "--auto-export-cook" };
        if (opts.ShaderOnly) injected.Add("--shader-only");
        if (opts.SkipGlobal) injected.Add("--skip-global");
        if (opts.KeepAlive) injected.Add("--no-quit");
        injected.Add("--ready-timeout-sec");
        injected.Add(opts.ReadyTimeoutSec.ToString());
        if (opts.SplitVariants is true) injected.Add("--split-variants");
        if (opts.SplitVariants is false) injected.Add("--no-split-variants");

        // The hook re-reads Environment.GetCommandLineArgs() inside its
        // Initialize() method. .NET caches the result, but the cache is
        // populated on first call, so as long as we set this BEFORE
        // RuriHook.ApplyHooks runs the hook's Initialize, the synthesised
        // args land. Initialize fires inside ApplyHooks → this mutation
        // would be too late from this method. Instead, override what the
        // CLR returns next time by re-launching with a synthesised args
        // env var that the hook can opt into.
        //
        // Practical workaround: stash the synthesised flags in an env var
        // and have the hook check it. Until that's wired (separate hook
        // change), the CLI directly forces the auto-export by reaching
        // into the hook's static state via the same public API the
        // existing --split-variants flag uses.
        Environment.SetEnvironmentVariable("RURI_FMODELHOOK_AUTOEXPORT_ARGS", string.Join(" ", injected));

        // Force the hook into auto-export mode regardless of the env-var
        // mechanism above — this is the belt-and-braces guarantee that a
        // CLI invocation always runs the export.
        ForceAutoExportHook(opts);
    }

    // Reaches into the AutoExport hook via reflection to set the same
    // private fields its CLI parser would set, BEFORE the hook's
    // Initialize() runs. Keeping it reflection-based avoids broadening
    // the public surface of the hook for a CLI-internal needs.
    private static void ForceAutoExportHook(CliOptions opts)
    {
        try
        {
            Type? hookType = Type.GetType("Ruri.FModelHook.Game.SBUE.AutoExport.UE_ShaderDecompiler_AutoExport_Hook, Ruri.FModelHook");
            if (hookType == null) return;

            void Set(string field, object value)
            {
                FieldInfo? f = hookType.GetField(field, BindingFlags.Static | BindingFlags.NonPublic);
                f?.SetValue(null, value);
            }

            Set("_autoExportRequested", true);
            if (opts.ShaderOnly) Set("_shaderOnly", true);
            if (opts.SkipGlobal) Set("_skipGlobal", true);
            // Default: quit when done (keep-alive overrides).
            Set("_quitWhenDone", !opts.KeepAlive);
            Set("_readyTimeoutSec", opts.ReadyTimeoutSec);
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[Ruri.FModelHook.CLI] Failed to force auto-export hook state: {ex.Message}");
        }
    }

    private static void EnsureHookAssembliesLoaded()
    {
        // Matches the GUI's belt-and-braces approach. The typeof() pin is
        // enough on most configs but Assembly.Load by name is the
        // canonical resolver fallback if the JIT skips type metadata for
        // an unreferenced type.
        _ = typeof(Ruri.FModelHook.GameType);
        _ = typeof(Ruri.FModelHook.Game.SBUE.ShaderDecompiler.UE_ShaderDecompiler_Hook);
        _ = typeof(Ruri.FModelHook.Game.SBUE.AutoExport.UE_ShaderDecompiler_AutoExport_Hook);
        try { Assembly.Load("Ruri.FModelHook"); } catch { /* logged below if 0 hooks */ }

        int hookCount = RuriHook.GetAvailableHooks().Count;
        HookLogger.Log($"[Ruri.FModelHook.CLI] Hook assemblies loaded — discovered {hookCount} [GameHookAttribute] type(s).");
        if (hookCount == 0)
        {
            HookLogger.LogFailure("[Ruri.FModelHook.CLI] No hooks discovered. Check that Ruri.FModelHook.dll sits next to Ruri.FModelHook.CLI.exe.");
        }
    }

    // Boots FModel's WPF App. The window is hidden by default (a full
    // headless WPF still needs a Dispatcher loop running for the
    // hook-installed MainWindow.OnLoaded detour to fire and the
    // auto-export to begin), and we shut it down after auto-export
    // unless --keep-alive was passed.
    private static void LaunchFModel(CliOptions opts)
    {
        HookLogger.Log("[Ruri.FModelHook.CLI] Launching FModel (headless)...");
        var app = new FModel.App();
        app.InitializeComponent();

        // Hide the main window as soon as it materialises. We can't simply
        // suppress it — FModel's startup wires the provider/AES dialogs
        // off the MainWindow.OnLoaded path, which IS the trigger for the
        // auto-export hook. Showing-then-hiding is the cheapest reliable
        // way to keep the dispatcher alive without a visible window.
        if (!opts.ShowWindow)
        {
            app.Activated += (_, _) => HideAllWindows();
            app.Startup += (_, _) =>
            {
                // The MainWindow may not exist yet at Startup; defer the
                // first hide to the next dispatcher cycle, when WPF has
                // had a chance to instantiate it.
                _ = app.Dispatcher.BeginInvoke(new Action(HideAllWindows), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
        }

        app.Run();
    }

    private static void HideAllWindows()
    {
        if (Application.Current == null) return;
        foreach (Window w in Application.Current.Windows)
        {
            try
            {
                w.WindowState = WindowState.Minimized;
                w.ShowInTaskbar = false;
                w.Visibility = Visibility.Hidden;
                w.Hide();
            }
            catch { /* harmless if a window has already closed itself */ }
        }
    }
}
