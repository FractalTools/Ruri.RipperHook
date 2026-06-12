using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using FModel;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using Ruri.FModelHook.Attributes;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.GlbSceneExport
{
    // Default-on, toggleable (via Hooks > Enabled Hooks...) interactive hook
    // that adds "Export GLB Scene" to the asset-list right-click menu for
    // .umap selections. It reuses FModel's verified world preview LOGIC
    // (WorldActorCollector + WorldGlbExporter port Renderer.LoadWorld /
    // WorldMesh / CalculateTransform) but, unlike the preview, also resolves
    // World Partition content so the whole map exports — not just the handful
    // of actors cooked into the top-level .umap.
    //
    // Detours MainWindow.OnLoaded (prefix-continue) the same way HookMenuBootstrap
    // does; multiple OnLoaded detours coexist. The menu
    // item is injected via a global ContextMenu.OpenedEvent class handler
    // because FModel's FileContextMenu is x:Shared="False" (a fresh instance per
    // control), so there is no single menu object to hold a reference to.
    [FModelHook(GameType.UE_GlbSceneExport)]
    public sealed class UE_GlbSceneExport_Hook : RuriHook
    {
        private const string MenuItemTag = "Ruri.GlbSceneExport";
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
                HookLogger.LogSuccess("[GlbScene] Hook armed — right-click a .umap and choose 'Export GLB Scene'.");
            }
            catch (Exception ex)
            {
                HookLogger.LogFailure($"[GlbScene] Failed to register context-menu handler: {ex.Message}");
            }
        }

        private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu || menu.PlacementTarget is not ListBox listBox) return;

            // Only decorate the asset list (items are GameFileViewModel). Other
            // ListBox context menus in the app are left untouched.
            var selectedMaps = listBox.SelectedItems
                .OfType<GameFileViewModel>()
                .Where(viewModel => viewModel.Asset.Extension.Equals("umap", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (selectedMaps.Count == 0)
            {
                RemoveExistingItem(menu);
                return;
            }

            if (menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, MenuItemTag))) return;

            MenuItem exportItem = new()
            {
                Header = selectedMaps.Count == 1 ? "Export GLB Scene" : $"Export GLB Scene ({selectedMaps.Count} maps)",
                Tag = MenuItemTag,
            };
            exportItem.Click += (_, _) => StartExport(selectedMaps.Select(viewModel => viewModel.Asset.Path).ToList());
            menu.Items.Add(exportItem);
        }

        private static void RemoveExistingItem(ContextMenu menu)
        {
            var existing = menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, MenuItemTag));
            if (existing != null) menu.Items.Remove(existing);
        }

        private static void StartExport(List<string> mapPaths)
        {
            if (Interlocked.Exchange(ref _exportInProgress, 1) == 1)
            {
                HookLogger.Log("[GlbScene] An export is already running; ignoring the new request.");
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    RunExport(mapPaths, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    HookLogger.LogFailure($"[GlbScene] Export crashed: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _exportInProgress, 0);
                }
            });
        }

        private static void RunExport(List<string> mapPaths, CancellationToken cancellationToken)
        {
            var vm = ApplicationService.ApplicationView?.CUE4Parse;
            if (vm?.Provider == null)
            {
                HookLogger.LogFailure("[GlbScene] No provider mounted — load a game first.");
                return;
            }

            ExporterOptions options = UserSettings.Default.ExportOptions;
            options.MeshFormat = EMeshFormat.Gltf2;
            // Export geometry + material NAMES (baked into the glTF primitives)
            // but NOT decoded texture sidecars. Bulk texture decode across a
            // whole open world is intermittently crash-prone — a thread-safety
            // race in CUE4Parse's parallel native texture decode — and a hard
            // native crash here would take down the whole FModel process. The
            // scene geometry is the deliverable; textures are re-linked by
            // material name via FModel's normal per-asset texture export, or via
            // the headless CLI: 'Ruri.FModelHook.CLI --export-map-direct --with-materials'.
            options.ExportMaterials = false;
            string outputDirectory = UserSettings.Default.ModelDirectory;

            foreach (string mapPath in mapPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var package = vm.Provider.LoadPackage(mapPath);
                    UWorld? world = package.GetExports().OfType<UWorld>().FirstOrDefault();
                    if (world == null)
                    {
                        HookLogger.LogFailure($"[GlbScene] '{mapPath}' has no UWorld export; skipped.");
                        continue;
                    }

                    // A fresh exporter per map keeps each .glb scene self-contained.
                    // Pass the provider Files key (mapPath) so World Partition cell
                    // scans match the file table, not the logical "/Game/..." path.
                    var perMap = new WorldGlbExporter(vm.Provider, options, HookLogger.Log, HookLogger.LogFailure);
                    perMap.Export(world, mapPath, outputDirectory, cancellationToken);
                }
                catch (Exception ex)
                {
                    HookLogger.LogFailure($"[GlbScene] '{mapPath}' failed: {ex.Message}");
                }
            }
        }
    }
}
