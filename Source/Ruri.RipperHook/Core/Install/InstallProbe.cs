using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.Primitives;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;

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

    /// <summary>PlayerSettings.bundleVersion -- the game's own version, "" when it states none.</summary>
    public required string GameVersion { get; init; }

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
    private const string DataBundleName = "data.unity3d";
    private const string ReadEngineVersionMethod = "ReadEngineVersion";
    private const int SearchWindow = 0x10000;
    private const int VersionWindow = 0x200;
    private const int MaxStringLength = 128;

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
                GameVersion = ReadGameVersion(Path.Combine(dataFolder, EngineSettingsName), product),
                EngineVersion = ReadEngineVersion(dataFolder, product),
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
    /// PlayerSettings.bundleVersion, read out of the engine settings asset's DATA section.
    ///
    /// PlayerSettings is that file's first object, so its bytes start at the data offset -- past
    /// everything a build might encipher (EXILIUM enciphers exactly the metadata in front of it
    /// and nothing else). Nothing here assumes an offset or a field layout: the anchor is the
    /// productName the SAME install published in app.info, matched as its own length-prefixed
    /// bytes, so a hit proves where PlayerSettings' strings are. bundleVersion is then the first
    /// version-shaped string after it, within a bounded window.
    ///
    /// Measured on 10 players across Unity 5.6 / 2019.4 / 2021.3, including a build whose
    /// metadata is enciphered. A build that states no version, or states one that is not
    /// version-shaped, answers "" -- which constrains nothing downstream.
    /// </summary>
    private static string ReadGameVersion(string engineSettingsPath, string product)
    {
        if (!File.Exists(engineSettingsPath) || product.Length == 0)
        {
            return string.Empty;
        }

        byte[] blob;
        try
        {
            using FileStream file = File.OpenRead(engineSettingsPath);
            blob = new byte[Math.Min(SearchWindow, file.Length)];
            int read = file.Read(blob, 0, blob.Length);
            if (read < blob.Length)
            {
                Array.Resize(ref blob, read);
            }
        }
        catch
        {
            return string.Empty;
        }

        int anchor = IndexOfString(blob, product);
        if (anchor < 0)
        {
            return string.Empty;
        }

        int cursor = anchor + 4 + product.Length;
        cursor += (4 - (cursor & 3)) & 3;
        int limit = Math.Min(blob.Length, cursor + VersionWindow);
        while (cursor + 4 < limit)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(cursor));
            if (length <= 0 || length > MaxStringLength || cursor + 4 + length > blob.Length)
            {
                cursor += 4;
                continue;
            }
            ReadOnlySpan<byte> text = blob.AsSpan(cursor + 4, length);
            if (!IsPrintable(text))
            {
                cursor += 4;
                continue;
            }
            string candidate = Encoding.ASCII.GetString(text);
            if (IsVersionShaped(candidate))
            {
                return candidate;
            }
            cursor += 4 + length;
            cursor += (4 - (cursor & 3)) & 3;
        }
        return string.Empty;
    }

    private static int IndexOfString(byte[] blob, string value)
    {
        byte[] needle = new byte[4 + value.Length];
        BinaryPrimitives.WriteInt32LittleEndian(needle, value.Length);
        Encoding.ASCII.GetBytes(value, 0, value.Length, needle, 4);
        return blob.AsSpan().IndexOf(needle);
    }

    private static bool IsPrintable(ReadOnlySpan<byte> text)
    {
        foreach (byte character in text)
        {
            if (character < 0x20 || character > 0x7e)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsVersionShaped(string text)
    {
        if (text.Length == 0 || !char.IsDigit(text[0]))
        {
            return false;
        }
        foreach (char character in text)
        {
            if (!char.IsLetterOrDigit(character) && character != '.')
            {
                return false;
            }
        }
        return text.Contains('.');
    }

    /// <summary>
    /// The Unity version this player's own files state, in the order a Unity build publishes
    /// it: the engine settings asset's serialized header, then the data bundle's header for a
    /// build that ships one instead. A build that transforms both answers only through its own
    /// game's code, which that game declares (<see cref="InstallVersionReaderAttribute"/>);
    /// "" means nothing readable said, which is a fact about the install, not an error.
    /// </summary>
    private static string ReadEngineVersion(string dataFolder, string product)
    {
        string engineSettings = Path.Combine(dataFolder, EngineSettingsName);
        if (File.Exists(engineSettings))
        {
            try
            {
                return SerializedFile.FromFile(engineSettings, LocalFileSystem.Instance).Version.ToString();
            }
            catch
            {
            }
        }

        string dataBundle = Path.Combine(dataFolder, DataBundleName);
        if (File.Exists(dataBundle))
        {
            try
            {
                using Stream stream = File.OpenRead(dataBundle);
                FileStreamBundleHeader header = new();
                header.Read(stream);
                return UnityVersion.Parse(header.UnityWebMinimumRevision).ToString();
            }
            catch
            {
            }
        }

        return ReadDeclaredEngineVersion(dataFolder, product);
    }

    private static string ReadDeclaredEngineVersion(string dataFolder, string product)
    {
        Type? reader = HookCatalog.VersionReaderFor(product);
        if (reader is null)
        {
            return string.Empty;
        }

        MethodInfo method = reader.GetMethod(ReadEngineVersionMethod, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"[InstallProbe] {reader.FullName} declares [InstallVersionReader] but has no "
                + $"public static string {ReadEngineVersionMethod}(string dataFolder).");

        return method.Invoke(null, new object[] { dataFolder }) as string ?? string.Empty;
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
