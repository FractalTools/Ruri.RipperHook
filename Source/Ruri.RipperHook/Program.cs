using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.GUI.Web;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using Ruri.Hook.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Windows.Forms;

namespace Ruri.RipperHook;

internal static class Program
{
    private const string ConfigFileName = "RuriRipperHook.json";

    [STAThread]
    public static void Main(string[] args)
    {
        // Handle assembly version mismatches (e.g. Ruri.SourceGenerated references older AssetRipper.Assets)
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            // Try to find already-loaded assembly with matching name (ignore version)
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.GetName().Name == name.Name)
                    return loaded;
            }
            return null;
        };
        var hookIds = new List<string>();
        string? loadPath = null;
        string? exportPath = null;
        var passthroughArgs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hook" when i + 1 < args.Length:
                    hookIds.Add(args[++i]);
                    break;
                case "--load" when i + 1 < args.Length:
                    loadPath = args[++i];
                    break;
                case "--export" when i + 1 < args.Length:
                    exportPath = args[++i];
                    break;
                default:
                    passthroughArgs.Add(args[i]);
                    break;
            }
        }

        bool cliMode = hookIds.Count > 0;
        HookConfig config;

        if (cliMode)
        {
            // CLI mode: skip GUI, apply specified hooks directly
            config = new HookConfig();
            foreach (var id in hookIds)
                config.EnabledHooks.Add(id);
            Console.WriteLine($"[Ruri] CLI mode: hooks={string.Join(", ", hookIds)}");
        }
        else
        {
            // GUI mode: original behavior
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
            config = HookConfig.Load(configPath);
            Application.Run(new Ruri.Hook.UI.HookSelectionForm(config, configPath));
        }

        Ruri.Hook.RuriHook.ApplyHooks(config);

        if (loadPath != null)
        {
            // Headless test mode: load → process → export → exit
            RunHeadlessTest(loadPath, exportPath);
        }
        else
        {
            // AssetRipper Web UI (pass --headless automatically in CLI mode)
            if (cliMode && !passthroughArgs.Contains("--headless"))
                passthroughArgs.Add("--headless");
            WebApplicationLauncher.Launch(passthroughArgs.ToArray());
        }
    }

    /// <summary>
    /// Headless test: load game files, report asset stats, optionally export.
    /// Usage: --hook EndField_1.1.9 --load "C:\path\to\vfs" --export "C:\output"
    /// </summary>
    private static void RunHeadlessTest(string loadPath, string? exportPath)
    {
        Logger.Add(new ConsoleLogger());
        Logger.Add(new FileLogger($"Ruri_Test_{DateTime.Now:yyyyMMdd_HHmmss}.log"));

        Console.WriteLine($"[Ruri] Loading: {loadPath}");

        string[] paths;
        if (Directory.Exists(loadPath))
        {
            var files = Directory.GetFiles(loadPath);
            var dirs = Directory.GetDirectories(loadPath);
            paths = files.Concat(dirs).ToArray();
        }
        else if (File.Exists(loadPath))
        {
            paths = new[] { loadPath };
        }
        else
        {
            Console.Error.WriteLine($"[Ruri] Path not found: {loadPath}");
            Environment.Exit(1);
            return;
        }

        try
        {
            var settings = new FullConfiguration();
            settings.ExportSettings.ShaderExportMode = ShaderExportMode.Decompile;
            settings.LogConfigurationValues();
            var handler = new ExportHandler(settings);
            GameData gameData = handler.Load(paths, LocalFileSystem.Instance);
            handler.Process(gameData);

            // Report loaded assets
            int totalAssets = 0;
            int shaderCount = 0;
            foreach (var collection in gameData.GameBundle.FetchAssetCollections())
            {
                foreach (var asset in collection)
                {
                    totalAssets++;
                    if (asset.ClassID == 48) // Shader
                        shaderCount++;
                }
            }

            Console.WriteLine($"[Ruri] Loaded {totalAssets} assets ({shaderCount} Shaders)");

            if (exportPath != null)
            {
                Console.WriteLine($"[Ruri] Exporting to: {exportPath}");
                if (Directory.Exists(exportPath))
                    Directory.Delete(exportPath, true);
                Directory.CreateDirectory(exportPath);
                handler.Export(gameData, exportPath, LocalFileSystem.Instance);
                Console.WriteLine("[Ruri] Export complete.");
            }

            Environment.Exit(shaderCount > 0 ? 0 : 2);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Ruri] Error: {ex}");
            Environment.Exit(1);
        }
    }
}
