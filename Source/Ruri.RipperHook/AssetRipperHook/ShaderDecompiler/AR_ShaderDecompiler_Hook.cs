using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent;
using AssetRipper.SourceGenerated.Classes.ClassID_48;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.HookUtils.ExportHandlerHook;
﻿namespace Ruri.RipperHook.AR;

/// <summary>
/// DXBC反编译为hlsl用的
/// </summary>
[RipperHook(GameType.AR_ShaderDecompiler)]
public partial class AR_ShaderDecompiler_Hook : RipperHookCommon
{
    protected override void InitAttributeHook()
    {
        RipperPrimaryAssetExportService.CustomContentExtractors.Add(ShaderExtractor);
        base.InitAttributeHook();
    }

    private static IEnumerable<(Type AssetType, IContentExtractor Extractor)> ShaderExtractor(FullConfiguration settings)
    {
        yield return (typeof(IShader), ShaderContentExtractor.Instance);
    }
}
