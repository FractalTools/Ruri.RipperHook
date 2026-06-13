using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FModel;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using Ruri.FModelHook.Attributes;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.UnityExport.Hook;

// Default-on, toggleable interactive hook that adds "Export -> Unity YAML" to the
// asset-list right-click menu for .uasset / .umap selections. On click it loads
// each selected package and runs the SAME UnityExport mapper the headless CLI
// uses (UnityYamlExportRunner.ConvertAndExport), writing .asset/.mat/.anim/.prefab
// + .meta under <ModelDirectory>/UnityExport.
//
// Injection mirrors UE_GlbSceneExport_Hook exactly: detour MainWindow.OnLoaded
// (prefix-continue) and install a GLOBAL ContextMenu.OpenedEvent class handler,
// because FModel's FileContextMenu is x:Shared="False" (a fresh instance per
// control) so there is no single menu object to hold. FModel source is NOT
// modified. In the headless CLI this hook is inert (MainWindow never loads).
[FModelHook(GameType.UE_UnityYamlExport)]
public sealed class UE_UnityYamlExport_Hook : RuriHook
{
    private const string MenuItemTag = "Ruri.UnityYamlExport";
    private static int _runOnceGuard;
    private static int _exportInProgress;

    [RetargetMethod(typeof(MainWindow), "OnLoaded", true, false)]
    public static void OnLoaded_Before(MainWindow self, object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _runOnceGuard, 1) == 1) return;

        try
        {
            EventManager.RegisterClassHandler(
                typeof(ContextMenu),
                ContextMenu.OpenedEvent,
                new RoutedEventHandler(OnContextMenuOpened));
            HookLogger.LogSuccess("[UnityExport] Hook armed — right-click a .uasset/.umap and choose 'Export → Unity YAML'.");
        }
        catch (Exception ex)
        {
            HookLogger.LogFailure($"[UnityExport] Failed to register context-menu handler: {ex.Message}");
        }
    }

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not ListBox listBox) return;

        var selected = listBox.SelectedItems
            .OfType<GameFileViewModel>()
            .Where(viewModel => viewModel.Asset.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase)
                                || viewModel.Asset.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (selected.Count == 0)
        {
            RemoveExistingItem(menu);
            return;
        }

        if (menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, MenuItemTag))) return;

        MenuItem exportItem = new()
        {
            Header = selected.Count == 1 ? "Export → Unity YAML" : $"Export → Unity YAML ({selected.Count} assets)",
            Tag = MenuItemTag,
        };
        exportItem.Click += (_, _) => StartExport(selected.Select(viewModel => viewModel.Asset.Path).ToList());
        menu.Items.Add(exportItem);
    }

    private static void RemoveExistingItem(ContextMenu menu)
    {
        var existing = menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, MenuItemTag));
        if (existing != null) menu.Items.Remove(existing);
    }

    private static void StartExport(List<string> assetPaths)
    {
        if (Interlocked.Exchange(ref _exportInProgress, 1) == 1)
        {
            HookLogger.Log("[UnityExport] An export is already running; ignoring the new request.");
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                RunExport(assetPaths);
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"[UnityExport] Export crashed: {ex}");
            }
            finally
            {
                Interlocked.Exchange(ref _exportInProgress, 0);
            }
        });
    }

    private static void RunExport(List<string> assetPaths)
    {
        var viewModel = ApplicationService.ApplicationView?.CUE4Parse;
        if (viewModel?.Provider == null)
        {
            HookLogger.LogFailure("[UnityExport] No provider mounted — load a game first.");
            return;
        }

        string outputDirectory = Path.Combine(UserSettings.Default.ModelDirectory, "UnityExport");
        Directory.CreateDirectory(outputDirectory);

        UnityYamlExportRunner.RunResult result = UnityYamlExportRunner.ConvertAndExport(
            viewModel.Provider,
            assetPaths,
            outputDirectory,
            unityVersionText: null,
            HookLogger.Log,
            HookLogger.LogFailure);

        HookLogger.LogSuccess($"[UnityExport] Done. assets={assetPaths.Count} converted={result.Converted} " +
                              $"files={result.FilesWritten} -> {outputDirectory}");
    }
}
