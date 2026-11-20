using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.IO.Files;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace Ruri.RipperHook.RuriEngine;

[RipperFeature("RuriEngine")]
public partial class AR_RuriEngine_Hook : RipperHookCommon
{
    protected AR_RuriEngine_Hook()
    {
    }

    protected override void InitAttributeHook()
    {
        RuriEngineSession.Reset();
        HookManager.RegisterCleanup(RuriEngineSession.Reset);
        base.InitAttributeHook();
    }

    [RetargetMethod(typeof(ProjectExporter), nameof(ProjectExporter.DoFinalOverrides), isBefore: false, isReturn: false)]
    public static void ProjectExporter_DoFinalOverrides(ProjectExporter exporter, FullConfiguration settings)
    {
        exporter.OverrideExporter<IUnityObjectBase>(
            new RuriEngineExporter(RuriEngineSession.Mapping, RuriEngineSession.Types, RuriEngineSession.Gaps),
            allowInheritance: true);
    }

    [RetargetMethod(typeof(ExportCollection), "ExportMeta")]
    public static void ExportCollection_ExportMeta(IExportContainer container, Meta meta, string filePath, FileSystem fileSystem)
    {
        UnityYamlNode document = MetaJson.FromAssetRipper(meta.ExportYamlDocument(container).Root!);
        fileSystem.File.WriteAllBytes(filePath + ".meta", MetaJson.Write(document, RuriEngineSession.Mapping));
    }

    [RetargetMethod(typeof(CoreConfiguration), "get_ProjectRootPath")]
    public static string CoreConfiguration_ProjectRootPath(CoreConfiguration configuration)
    {
        return configuration.ExportRootPath;
    }

    [RetargetMethodFunc(typeof(ExportHandler), "GetPostExporters")]
    public static bool ExportHandler_GetPostExporters(ILContext il)
    {
        ILCursor cursor = new(il);
        int injected = 0;
        while (cursor.TryGotoNext(MoveType.Before, static instruction => instruction.OpCode == OpCodes.Ret))
        {
            cursor.Emit(OpCodes.Pop);
            cursor.EmitDelegate<Func<IEnumerable<IPostExporter>>>(PostExporters);
            cursor.Index++;
            injected++;
        }
        return injected > 0;
    }

    private static IEnumerable<IPostExporter> PostExporters()
    {
        return [new RuriEnginePostExporter(RuriEngineSession.Mapping, RuriEngineSession.Gaps)];
    }
}
