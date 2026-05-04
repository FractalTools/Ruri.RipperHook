using AssetRipper.Import.Logging;

namespace Ruri.RipperHook.CLI;

/// <summary>
/// AssetRipper log sink that writes everything to stderr (so stdout stays clean for the JSON
/// summary). Honors <see cref="MinLevel"/> as a hard threshold; the AssetRipper Logger.AllowVerbose
/// already gates Verbose globally.
/// </summary>
internal sealed class StderrLogger : ILogger
{
    public LogType MinLevel { get; init; } = LogType.Info;

    public void Log(LogType type, LogCategory category, string message)
    {
        if (!ShouldEmit(type)) return;

        TextWriter writer = Console.Error;
        ConsoleColor previous = Console.ForegroundColor;
        try
        {
            switch (type)
            {
                case LogType.Debug: Console.ForegroundColor = ConsoleColor.DarkBlue; break;
                case LogType.Verbose: Console.ForegroundColor = ConsoleColor.DarkGray; break;
                case LogType.Warning: Console.ForegroundColor = ConsoleColor.DarkYellow; break;
                case LogType.Error: Console.ForegroundColor = ConsoleColor.DarkRed; break;
            }

            if (category == LogCategory.None)
            {
                writer.WriteLine(message);
            }
            else
            {
                writer.WriteLine($"{category} : {message}");
            }
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    public void BlankLine(int numLines)
    {
        for (int i = 0; i < numLines; i++)
        {
            Console.Error.WriteLine();
        }
    }

    private bool ShouldEmit(LogType type)
    {
        // LogType ordering (per AssetRipper): Debug < Verbose < Info < Warning < Error.
        // We rank manually because LogType is not an ordered enum in AR.
        return Rank(type) >= Rank(MinLevel);
    }

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
