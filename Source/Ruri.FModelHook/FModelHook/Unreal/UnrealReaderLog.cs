using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// The Unreal reader's own log, given somewhere to go.
///
/// CUE4Parse reports through Serilog, and Serilog with no logger configured is the silent one:
/// an archive that failed to mount, a container whose global data is missing, a package that
/// failed to read all report nothing at all, and a run reads as "this build is empty" rather
/// than as the diagnosis it actually produced. An archive that would not open because a codec's
/// dependency did not bind is worth exactly as much as the line that says so.
///
/// It goes to the CONSOLE, in the reader's own words -- not into this host's asset log. Serilog
/// is the reader's logging, that one is the ripper's, and routing either into the other makes a
/// change in one a change in the other. Same shape as the FModel GUI's own
/// <c>ConsoleLogSinkHook</c>, which does this after that app starts; this one does it for the
/// hosts where that app never runs.
/// </summary>
public static class UnrealReaderLog
{
    private const string Template = "{Timestamp:HH:mm:ss} [{Level:u3}] [Unreal.Reader]: {Message:lj}{NewLine}{Exception}";

    private static bool installed;

    public static void Install()
    {
        if (installed)
        {
            return;
        }
        installed = true;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console(outputTemplate: Template, theme: AnsiConsoleTheme.Literate)
            .CreateLogger();
    }
}
