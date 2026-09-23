using Ruri.RipperHook;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.BlenderBridge.Data;
using Ruri.RipperHook.HookUtils.GameBundleHook;

namespace Ruri.FModelHook.BlenderBridge;

/// <summary>
/// The decoder for every Unreal build: its container is CUE4Parse's provider, and what it holds
/// is published as data -- placements, mesh buffers, reference skeletons, material parameters,
/// texture pixels and animation curves -- for a host to build from directly. Nothing is turned
/// into another engine's assets on the way. Declared for the engine family, so any title
/// without a decoder of its own is read through it.
/// </summary>
[RipperHook(Ruri.RipperHook.GameType.UnrealEngine)]
public partial class UnrealEngine_Hook : RipperHookCommon
{
    protected UnrealEngine_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        ApplyCapabilities(Ruri.RipperHook.GameType.UnrealEngine);

        UnrealReaderLog.Install();
        GameBundleHook.ScanIncludeFile = UnrealInstall.IsArchive;
        GameBundleHook.ScanChunkFull = UnrealArchiveScan.ScanFull;

        Registry.ApplyTypeHooks(typeof(UnrealSerializationDialect));
        UnrealDatasets.Register();
        UnrealShaders.Register();
        UnrealStatementSource.Register();
        Session.DeclareLayout(UnrealInstall.ContentRoots);
        Session.Opened += ClaimDatasets;
        ClaimDatasets();
        Ruri.Hook.Core.HookManager.RegisterCleanup(() =>
        {
            Session.Opened -= ClaimDatasets;
            UnrealStatementSource.Unregister();
            UnrealTitleDatasets.Forget();
            Datasets.Clear(UnrealDatasets.IdPrefix);
            Session.ForgetLayout();
            UnrealProviderSession.Close();
        });
        base.InitAttributeHook();
    }

    /// <summary>Let the title this session opened on replace what the family published for it.</summary>
    private static void ClaimDatasets()
    {
        if (UnrealTitles.For(Session.GameRoot) is { } title)
        {
            UnrealTitleDatasets.PublishFor(title.Product);
        }
    }
}
