using System;

namespace Ruri.RipperHook.HookUtils.ExportHandlerHook;

public sealed class AssetProcessorRegistration
{
    public required Type InsertBefore { get; init; }

    public required ExportHandlerHook.AssetProcessorDelegate Factory { get; init; }
}
