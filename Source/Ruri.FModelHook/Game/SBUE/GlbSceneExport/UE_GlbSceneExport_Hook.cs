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
using CUE4Parse_Conversion.Options;
using FModel;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using Ruri.FModelHook.Attributes;
using Ruri.Hook;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport
{
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

            ExportOptions userOptions = UserSettings.GetExportOptions();
            var options = new ExportOptions(
                meshFormat: EMeshFormat.Gltf2,
                naniteMeshFormat: userOptions.NaniteMeshFormat,
                meshQuality: userOptions.MeshQuality,
                texturePlatform: userOptions.TexturePlatform,
                textureFormat: userOptions.TextureFormat,
                textureQuality: userOptions.TextureQuality,
                exportHdrTexturesAsHdr: userOptions.ExportHdrTexturesAsHdr,
                materialDepth: userOptions.MaterialDepth,
                exportMaterials: false,
                exportMorphTargets: userOptions.ExportMorphTargets,
                socketFormat: userOptions.SocketFormat,
                compressionFormat: userOptions.CompressionFormat,
                exportAllTextureMips: userOptions.ExportAllTextureMips);
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
