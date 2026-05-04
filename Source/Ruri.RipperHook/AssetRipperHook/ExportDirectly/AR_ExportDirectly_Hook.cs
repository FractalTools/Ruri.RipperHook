using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.HookUtils.ExportHandlerHook;
﻿namespace Ruri.RipperHook.AR;

/// <summary>
/// 直接导出到当前游戏目录名_Output文件夹下
/// 无人值守模式
/// </summary>
[RipperHook(GameType.AR_ExportDirectly)]
public partial class AR_ExportDirectly_Hook : RipperHookCommon
{
}
