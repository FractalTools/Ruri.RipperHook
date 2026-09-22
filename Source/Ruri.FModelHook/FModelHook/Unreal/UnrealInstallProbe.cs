using AssetRipper.Import.Logging;
using CUE4Parse.FileProvider;
using Ruri.RipperHook.Attributes;
using Ruri.RipperHook.Core.Install;
using UE4Config.Parsing;

namespace Ruri.FModelHook.Unreal;

/// <summary>
/// What a packaged Unreal install says it is. The archives' own mount point names the project
/// (CUE4Parse's ProjectName reads it off the directory index), the project's DefaultGame.ini
/// inside those archives names the company and the project's own version, and the build version
/// literal compiled into the executable names the engine. Asked without any decoder selected -- an install whose
/// archives need a key answers with what it can read (the engine, the folder) and states no
/// project until the key is given through the source options.
///
/// A build a <see cref="UnrealTitle"/> claims answers from the claim alone and nothing is
/// mounted: the title recognised it by files on disk, so opening its archives to read back a
/// name already in hand would make NAMING an install cost as much as READING one -- and a title
/// that re-keys every archive would fetch thousands of keys to answer a question about a folder.
/// </summary>
[RipperInstallProbe]
public static class UnrealInstallProbe
{
    public const string EngineFamily = nameof(Ruri.RipperHook.GameType.UnrealEngine);
    private const string ProjectSettingsSection = "/Script/EngineSettings.GeneralProjectSettings";
    private const string ProjectVersionKey = "ProjectVersion";
    private const string CompanyNameKey = "CompanyName";

    public static IEnumerable<PlayerIdentity> Probe(string gameRoot)
    {
        string[] pakFolders = UnrealInstall.PakFolders(gameRoot);
        if (pakFolders.Length == 0)
        {
            yield break;
        }
        string engineVersion = UnrealInstall.EngineVersion(pakFolders[0]);
        UnrealTitles.Claim? claim = UnrealTitles.Of(gameRoot);
        if (claim is not null)
        {
            yield return new PlayerIdentity
            {
                DataFolder = pakFolders[0],
                Company = string.Empty,
                Product = claim.Title.Product,
                GameVersion = claim.Version,
                EngineVersion = engineVersion,
                Engine = EngineFamily,
            };
            yield break;
        }
        string product = string.Empty;
        string company = string.Empty;
        string projectVersion = string.Empty;
        try
        {
            DefaultFileProvider provider = UnrealProviderSession.Open(gameRoot);
            product = provider.ProjectName;
            company = IniValue(provider, CompanyNameKey);
            projectVersion = IniValue(provider, ProjectVersionKey);
        }
        catch (Exception exception)
        {
            Logger.Warning(LogCategory.Import, $"[Unreal] Could not mount '{gameRoot}' to read its identity: {exception.Message}");
        }
        if (product.Length == 0)
        {
            product = Path.GetFileName(UnrealInstall.ProjectFolder(pakFolders[0]) ?? string.Empty);
        }
        yield return new PlayerIdentity
        {
            DataFolder = pakFolders[0],
            Company = company,
            Product = product,
            GameVersion = projectVersion,
            EngineVersion = engineVersion,
            Engine = EngineFamily,
        };
    }

    private static string IniValue(DefaultFileProvider provider, string key)
    {
        List<InstructionToken> instructions = new();
        provider.DefaultGame.FindPropertyInstructions(ProjectSettingsSection, key, instructions);
        return instructions.Count > 0 ? instructions[0].Value?.Trim() ?? string.Empty : string.Empty;
    }
}
