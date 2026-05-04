using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.HookUtils.ExportHandlerHook;
﻿namespace Ruri.RipperHook.AR;

/// <summary>
/// DXBC反编译为hlsl用的
/// </summary>
[RipperHook(GameType.AR_ShaderDecompiler)]
public partial class AR_ShaderDecompiler_Hook : RipperHookCommon
{
}
