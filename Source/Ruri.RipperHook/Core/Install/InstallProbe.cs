using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;

namespace Ruri.RipperHook.Core.Install;

/// <summary>
/// What ONE Unity player says it is, read off the player itself. Every field here is the
/// engine's own published value, so a decoder joins to an install by string equality with
/// nothing translating in between.
/// </summary>
public sealed class PlayerIdentity
{
    public required string DataFolder { get; init; }

    /// <summary>PlayerSettings.companyName, as app.info publishes it.</summary>
    public required string Company { get; init; }

    /// <summary>PlayerSettings.productName -- the game's own word for itself, and a decoder's GameName.</summary>
    public required string Product { get; init; }

    /// <summary>The Unity version this player's serialized files carry, "" when they are not plain.</summary>
    public required string EngineVersion { get; init; }
}

/// <summary>
/// Which game a folder holds, read from the two files every Unity player publishes about
/// itself and nothing else: no bundle scan, no cabmap, no decoder, no guessing from folder
/// names a repacker chose.
///
/// <c>&lt;Product&gt;_Data/app.info</c> is the engine's own identity file -- companyName then
/// productName, exactly as the editor had them -- and it stays plain in every build, including
/// ones whose assets are encrypted. <c>&lt;Product&gt;_Data/globalgamemanagers</c> is the engine
/// settings asset, and its serialized header states the Unity version; reading it costs the
/// header, not the game.
///
/// One install routinely ships several players (a game, its VR build, its studio), so this
/// answers with all of them and <see cref="Project"/> picks the one the install IS -- by the
/// install's own fields, never by a list of known games.
/// </summary>
public static class InstallProbe
{
    private const string DataSuffix = "_Data";
    private const string AppInfoName = "app.info";
    private const string EngineSettingsName = "globalgamemanagers";

    public static List<PlayerIdentity> Read(string gameRoot)
    {
        List<PlayerIdentity> players = new();
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return players;
        }

        foreach (string dataFolder in Directory.EnumerateDirectories(gameRoot, "*" + DataSuffix).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            string stem = Path.GetFileName(dataFolder);
            stem = stem.Substring(0, stem.Length - DataSuffix.Length);

            string company = string.Empty;
            string product = stem;
            string appInfo = Path.Combine(dataFolder, AppInfoName);
            if (File.Exists(appInfo))
            {
                string[] lines = File.ReadAllLines(appInfo);
                company = lines.Length > 0 ? lines[0].Trim() : string.Empty;
                if (lines.Length > 1 && lines[1].Trim().Length > 0)
                {
                    product = lines[1].Trim();
                }
            }

            players.Add(new PlayerIdentity
            {
                DataFolder = dataFolder,
                Company = company,
                Product = product,
                EngineVersion = ReadEngineVersion(Path.Combine(dataFolder, EngineSettingsName)),
            });
        }

        return players;
    }

    /// <summary>
    /// The ONE player an install is. A project's extra builds are named after it: either its
    /// companyName ends with the product ("illusion\Koikatu" ships "Koikatu" plus "CharaStudio"),
    /// or the product is the one the others extend ("KoikatsuSunshine" owns "KoikatsuSunshine_VR").
    /// </summary>
    public static PlayerIdentity? Project(string gameRoot)
    {
        List<PlayerIdentity> players = Read(gameRoot);
        if (players.Count <= 1)
        {
            return players.FirstOrDefault();
        }
        return NamedByCompany(players) ?? PrefixOwner(players);
    }

    /// <summary>
    /// The Unity version the engine settings asset states, or "" when it is not a plain
    /// serialized file -- a build that transforms its engine assets states no version to
    /// anyone but its own decoder, which is a fact about the install, not an error.
    /// </summary>
    private static string ReadEngineVersion(string engineSettingsPath)
    {
        if (!File.Exists(engineSettingsPath))
        {
            return string.Empty;
        }
        try
        {
            return SerializedFile.FromFile(engineSettingsPath, LocalFileSystem.Instance).Version.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static PlayerIdentity? NamedByCompany(List<PlayerIdentity> players)
    {
        List<string> companies = players
            .Where(static player => player.Company.Length > 0)
            .Select(static player => Squashed(player.Company))
            .ToList();

        foreach (PlayerIdentity player in players)
        {
            string key = Squashed(player.Product);
            if (key.Length > 0 && companies.Any(company => company.EndsWith(key, StringComparison.Ordinal)))
            {
                return player;
            }
        }
        return null;
    }

    private static PlayerIdentity? PrefixOwner(List<PlayerIdentity> players)
    {
        return players
            .OrderByDescending(player => players.Count(other =>
                !ReferenceEquals(other, player) && other.Product.StartsWith(player.Product, StringComparison.Ordinal)))
            .ThenBy(static player => player.Product.Length)
            .ThenBy(static player => player.Product, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// Alphanumerics only: one install spells the same separator "\" in PlayerSettings and "_"
    /// in app.info, so a comparison that keeps them compares the spelling, not the identity.
    /// </summary>
    private static string Squashed(string text)
    {
        return new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
