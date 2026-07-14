using AssetRipper.Import.Logging;

namespace Ruri.RipperHook.Bridge;

/// <summary>
/// AssetRipper log sink for an in-process pythonnet host: writes to stderr, which lands in Blender's own
/// console/terminal output. Without a sink attached, AssetRipper's Logger is a silent black hole.
/// </summary>
internal sealed class BridgeLogger : ILogger
{
    public LogType MinLevel { get; init; } = LogType.Info;

    public void Log(LogType type, LogCategory category, string message)
    {
        if (Rank(type) < Rank(MinLevel))
        {
            return;
        }
        Console.Error.WriteLine(category == LogCategory.None ? message : $"{category} : {message}");
    }

    public void BlankLine(int numLines)
    {
        for (int i = 0; i < numLines; i++)
        {
            Console.Error.WriteLine();
        }
    }

    // LogType is not declared in severity order in AssetRipper, so rank manually.
    private static int Rank(LogType type) => type switch
    {
        LogType.Debug => 0,
        LogType.Verbose => 1,
        LogType.Info => 2,
        LogType.Warning => 3,
        LogType.Error => 4,
        _ => 2,
    };
}
