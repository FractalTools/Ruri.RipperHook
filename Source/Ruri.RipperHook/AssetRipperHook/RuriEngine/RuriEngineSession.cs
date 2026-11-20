namespace Ruri.RipperHook.RuriEngine;

public static class RuriEngineSession
{
    private static readonly object Gate = new();

    private static string? mappingPath;
    private static string? typesPath;
    private static string? gapReportPath;
    private static Lazy<RuriEngineMapping> mapping = NewMapping();
    private static Lazy<EngineTypes> types = NewTypes();
    private static GapReport gaps = new();

    internal static RuriEngineMapping Mapping => mapping.Value;

    internal static EngineTypes Types => types.Value;

    internal static GapReport Gaps => gaps;

    internal static string? GapReportPath => gapReportPath;

    public static void Configure(string? mappingOverride, string? gapReportOverride, string? typesOverride)
    {
        lock (Gate)
        {
            mappingPath = mappingOverride;
            gapReportPath = gapReportOverride;
            typesPath = typesOverride;
            Reset();
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            mapping = NewMapping();
            types = NewTypes();
            gaps = new GapReport();
        }
    }

    private static Lazy<RuriEngineMapping> NewMapping() => new(() => RuriEngineMapping.Load(mappingPath));

    private static Lazy<EngineTypes> NewTypes() => new(() => EngineTypes.Load(typesPath));
}

public static class UnityProjectLayout
{
    private const string AssetsDirectoryName = "Assets";
    private const string ProjectSettingsDirectoryName = "ProjectSettings";

    public static bool IsProjectRoot(string path)
    {
        return Directory.Exists(Path.Combine(path, AssetsDirectoryName))
            && Directory.Exists(Path.Combine(path, ProjectSettingsDirectoryName));
    }
}
