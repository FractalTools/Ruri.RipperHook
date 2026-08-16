using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using Ruri.Hook.Attributes;
using Ruri.Hook.Core;

namespace Ruri.RipperHook.Core.Install;

/// <summary>
/// What ONE Unity player says it is, read off the player itself. Every field here comes from
/// that player's own PlayerSettings or its serialized header, so a decoder joins to an install
/// by string equality with nothing translating in between.
/// </summary>
public sealed class PlayerIdentity
{
    public required string DataFolder { get; init; }

    /// <summary>PlayerSettings.companyName, exactly as the build spells it.</summary>
    public required string Company { get; init; }

    /// <summary>PlayerSettings.productName -- the game's own word for itself, and a decoder's GameName.</summary>
    public required string Product { get; init; }

    /// <summary>PlayerSettings.bundleVersion -- the game's own version, "" when it states none.</summary>
    public required string GameVersion { get; init; }

    /// <summary>The Unity version this player's serialized files state.</summary>
    public required string EngineVersion { get; init; }
}

/// <summary>
/// Which game a folder holds, read from the ONE file every Unity player publishes about
/// itself: <c>&lt;Product&gt;_Data/globalgamemanagers</c>. Its serialized header states the
/// engine version and its first object IS PlayerSettings, which states the company, the
/// product and the game's own version. No bundle scan, no cabmap, no decoder, no guessing
/// from folder names a repacker chose, and no second copy of these fields anywhere.
///
/// A build that transforms what it publishes (EXILIUM does) is undone first by whichever
/// game declared that transform (<see cref="InstallVersionReaderAttribute"/>) -- asked without
/// any game being selected, because reading this file is what selects the game.
///
/// One install routinely ships several players (a game, its VR build, its studio), so this
/// answers with all of them and <see cref="Project"/> picks the one the install IS.
/// </summary>
public static class InstallProbe
{
    private const string DataSuffix = "_Data";
    private const string EngineSettingsName = "globalgamemanagers";
    private const string TryDecryptMethod = "TryDecrypt";
    private const int PlayerSettingsClassID = 129;
    private const int MaxStringLength = 128;

    public static List<PlayerIdentity> Read(string gameRoot)
    {
        List<PlayerIdentity> players = new();
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            return players;
        }

        foreach (string dataFolder in Directory.EnumerateDirectories(gameRoot, "*" + DataSuffix)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            PlayerIdentity? identity = ReadPlayer(dataFolder);
            if (identity is not null)
            {
                players.Add(identity);
            }
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

    private static PlayerIdentity? ReadPlayer(string dataFolder)
    {
        string path = Path.Combine(dataFolder, EngineSettingsName);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }

        SerializedFile? file = Parse(data, path);
        if (file is null)
        {
            foreach (Type reader in HookCatalog.EngineFileReaders)
            {
                if (Undo(reader, data))
                {
                    file = Parse(data, path);
                    break;
                }
            }
        }
        if (file is null)
        {
            return null;
        }

        ObjectInfo settings = default;
        bool found = false;
        foreach (ObjectInfo candidate in file.Objects)
        {
            if (candidate.ClassID == PlayerSettingsClassID)
            {
                settings = candidate;
                found = true;
                break;
            }
        }
        if (!found)
        {
            return null;
        }

        List<string> texts = ReadStrings(settings.ObjectData);
        if (texts.Count < 2)
        {
            return null;
        }

        return new PlayerIdentity
        {
            DataFolder = dataFolder,
            Company = texts[0],
            Product = texts[1],
            GameVersion = texts.Skip(2).FirstOrDefault(IsVersionShaped) ?? string.Empty,
            EngineVersion = file.Version.ToString(),
        };
    }

    private static SerializedFile? Parse(byte[] data, string path)
    {
        try
        {
            return SchemeReader.ReadFile(data, path, Path.GetFileName(path)) as SerializedFile;
        }
        catch
        {
            return null;
        }
    }

    private static bool Undo(Type reader, byte[] data)
    {
        MethodInfo method = reader.GetMethod(TryDecryptMethod, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"[InstallProbe] {reader.FullName} declares [InstallVersionReader] but has no "
                + $"public static bool {TryDecryptMethod}(byte[] data).");
        try
        {
            return method.Invoke(null, new object[] { data }) is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The length-prefixed strings inside ONE object, in the order it wrote them. PlayerSettings
    /// opens with companyName then productName, and states bundleVersion a little further on;
    /// everything between is fixed-size, so walking the object's own bytes needs no offset and no
    /// layout. Bounded by the object -- this is not a search through the file.
    /// </summary>
    private static List<string> ReadStrings(byte[] data)
    {
        List<string> found = new();
        int at = 0;
        while (at + 4 < data.Length && found.Count < 8)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
            if (length > 0 && length <= MaxStringLength && at + 4 + length <= data.Length &&
                IsPrintable(data.AsSpan(at + 4, length)))
            {
                found.Add(Encoding.ASCII.GetString(data, at + 4, length));
                at += 4 + length;
                at += (4 - (at & 3)) & 3;
                continue;
            }
            at += 4;
        }
        return found;
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
        if (text.Length == 0 || !char.IsDigit(text[0]) || !text.Contains('.'))
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
        return true;
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

    private static string Squashed(string text)
    {
        return new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}
