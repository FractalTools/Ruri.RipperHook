using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using Ruri.RipperHook.BlenderBridge.Data;

namespace Ruri.FModelHook.BlenderBridge;

/// <summary>
/// The values an Unreal install is READ with beyond its folder -- what FModel keeps per game
/// directory -- each a named source option the host states and the kernel carries verbatim
/// (<see cref="Session.Options"/>). This table is the ONE statement of the names, their kinds
/// and their defaults: the panel form is drawn from it as a dataset and the provider session
/// reads through it, so nothing else spells an option name.
/// </summary>
public static class UnrealSourceOptions
{
    public const string Engine = "unreal.engine";
    public const string TexturePlatform = "unreal.platform";
    public const string MainKey = "unreal.aes.main";
    public const string DynamicKeys = "unreal.aes.dynamic";
    public const string Mappings = "unreal.mappings";
    public const string CustomVersions = "unreal.versioning.custom";
    public const string OptionOverrides = "unreal.versioning.options";
    public const string MapStructTypes = "unreal.versioning.mapstructs";
    public const string ExtraDirectories = "unreal.paks.extra";
    public const string ReadShaderMaps = "unreal.read.shadermaps";
    public const string Codecs = "unreal.codecs";
    public const string AnimationSampleRate = "unreal.animation.samplerate";
    public const string AnimationTolerance = "unreal.animation.tolerance";
    public const string MaterialSemantics = "unreal.material.semantics";

    public const char EntrySeparator = ';';
    public const char ValueSeparator = '=';

    /// <summary>One value the decoder reads an install with. A required option is one the build cannot be read without; which options are required is the mounted build's to say (see <see cref="UnrealDatasets"/>).</summary>
    public sealed record Option(string Name, string Kind, string Default, string Choices, string Description);

    public const string KindText = "text";
    public const string KindFlag = "flag";
    public const string KindChoice = "choice";
    public const string KindPath = "path";
    public const string KindEntries = "entries";

    public static IReadOnlyList<Option> Schema { get; } =
    [
        new(Engine, KindChoice, string.Empty, string.Join('|', EngineNames()),
            "The Unreal version (CUE4Parse EGame) the packages were cooked with. Empty reads it off the build version literal every Unreal executable carries "
            + "(++UE5+Release-5.1-CL-23901901); required only for a build on a renamed engine branch, whose executable carries none."),
        new(TexturePlatform, KindChoice, nameof(ETexturePlatform.DesktopMobile), string.Join('|', Enum.GetNames<ETexturePlatform>()),
            "How textures were cooked: the platform whose swizzle and block layout the texel data follows."),
        new(MainKey, KindText, string.Empty, string.Empty,
            "The AES key (0x-prefixed hex) that opens the main archives. Empty for an unencrypted game."),
        new(DynamicKeys, KindEntries, string.Empty, string.Empty,
            "Per-archive AES keys as guid=key entries separated by ';', for archives encrypted with their own key (dynamic pak GUIDs)."),
        new(Mappings, KindPath, string.Empty, string.Empty,
            "The .usmap reflection dump (unversioned property schema) of this build. Without it, objects serialized without property tags cannot be read at all."),
        new(CustomVersions, KindEntries, string.Empty, string.Empty,
            "Custom version overrides as GUID=version entries separated by ';' (FModel's Versioning > Custom Versions)."),
        new(OptionOverrides, KindEntries, string.Empty, string.Empty,
            "Serialization option overrides as Name=true|false entries separated by ';' (FModel's Versioning > Options)."),
        new(MapStructTypes, KindEntries, string.Empty, string.Empty,
            "Map key/value struct overrides as MapName=KeyStruct,ValueStruct entries separated by ';' (FModel's Versioning > Map Struct Types)."),
        new(ExtraDirectories, KindEntries, string.Empty, string.Empty,
            "Additional archive folders outside the install (downloaded content, save-side paks) separated by ';'."),
        new(ReadShaderMaps, KindFlag, "false", string.Empty,
            "Deserialize the inline shader maps of materials. Costly, needed only for shader work."),
        new(MaterialSemantics, KindFlag, "true", string.Empty,
            "Read each material's compiled base pass out of the shipped shader library and trace which texture channels feed base colour, normal, metallic, roughness, occlusion, emissive and the opacity mask, so the Unity material states those parts by name. Implies reading shader maps."),
        new(Codecs, KindPath, string.Empty, string.Empty,
            "The folder holding the native codecs (Oodle, zlib-ng, Detex) archives and textures are decoded with. "
            + "Empty keeps them in the user's own local data, where each reader fetches the build it tracks on first use."),
        new(AnimationSampleRate, KindText, "0", string.Empty,
            "Frames per second animation clips are sampled at. 0 keeps every sequence's own target frame rate (a build may state 1920); a lower rate resamples the decoded tracks and shrinks the clips."),
        new(AnimationTolerance, KindText, "1", string.Empty,
            "How far a written animation curve may stray from the decoded samples, as a multiple of the error threshold the sequence's own codec compressed it at. 1 keeps every curve within the game's own precision (the decoded samples themselves carry that much quantization error, so it fits them closely); a larger multiple drops more keys; 0 keeps every sample."),
    ];

    /// <summary>The stated curve tolerance as a multiple of the codec's own error threshold, zero keeping every sample; text that is not a number is an error naming the option.</summary>
    public static float AnimationToleranceValue()
    {
        string value = Text(AnimationTolerance);
        if (value.Length == 0)
        {
            return 1f;
        }
        return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scale) && scale >= 0f
            ? scale
            : throw new FormatException($"[Unreal] '{AnimationTolerance}' must be a non-negative multiple of the codec's error threshold (0 keeps every sample); got '{value}'.");
    }

    /// <summary>The stated animation sample rate, or zero for "each sequence's own"; text that is not a number is an error naming the option.</summary>
    public static float AnimationSampleRateValue()
    {
        string value = Text(AnimationSampleRate);
        if (value.Length == 0)
        {
            return 0f;
        }
        return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rate) && rate >= 0f
            ? rate
            : throw new FormatException($"[Unreal] '{AnimationSampleRate}' must be a frame rate (frames per second, 0 for the sequence's own); got '{value}'.");
    }

    public static string Text(string name) => Text(name, UnrealTitles.For(Session.GameRoot));

    /// <summary>
    /// One option's value for a build the given title claims: what the host stated, else what
    /// that title PUBLISHES about this build, else the schema's default. A stated value always
    /// wins, so a published one is a starting point and never a ceiling.
    /// </summary>
    public static string Text(string name, UnrealTitle? title)
    {
        string value = Session.Option(name);
        if (value.Length > 0)
        {
            return value;
        }
        string published = Published(name, title);
        if (published.Length > 0)
        {
            return published;
        }
        foreach (Option option in Schema)
        {
            if (option.Name == name)
            {
                return option.Default;
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// What a title states for an option nobody typed. The ONE statement of which options a
    /// title can answer for, and it is only the ones that ARE text: the archive keys and the
    /// reflection schema are fetched into memory and never become a path or a string, so they
    /// reach the mount directly -- see <see cref="UnrealKeyring"/> and
    /// <see cref="AesKeys(UnrealTitle?)"/>.
    /// </summary>
    private static string Published(string name, UnrealTitle? title)
    {
        if (title is null)
        {
            return string.Empty;
        }
        return name switch
        {
            Engine => title.Game.ToString(),
            _ => string.Empty,
        };
    }

    public static bool Flag(string name) => Text(name) is "1" or "true" or "True";

    public static IEnumerable<(string Key, string Value)> Entries(string name)
    {
        foreach (string entry in Text(name).Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = entry.IndexOf(ValueSeparator);
            if (separator > 0)
            {
                yield return (entry[..separator].Trim(), entry[(separator + 1)..].Trim());
            }
            else
            {
                yield return (entry, string.Empty);
            }
        }
    }

    public static EGame? EngineChoice() => EngineChoice(UnrealTitles.For(Session.GameRoot));

    public static EGame? EngineChoice(UnrealTitle? title)
    {
        string chosen = Text(Engine, title);
        return chosen.Length > 0 && Enum.TryParse(chosen, ignoreCase: true, out EGame game) ? game : null;
    }

    public static ETexturePlatform TexturePlatformChoice() =>
        Enum.TryParse(Text(TexturePlatform), ignoreCase: true, out ETexturePlatform platform) ? platform : ETexturePlatform.DesktopMobile;

    public static IEnumerable<KeyValuePair<FGuid, FAesKey>> AesKeys() => AesKeys(UnrealTitles.For(Session.GameRoot));

    /// <summary>
    /// Every key an install is opened with: the ones stated in the form first, then the ones the
    /// title publishes. A stated key wins by being submitted first; the published ones arrive as
    /// the whole history the title keeps, and the archive guids pick out the few that apply.
    /// </summary>
    public static IEnumerable<KeyValuePair<FGuid, FAesKey>> AesKeys(UnrealTitle? title)
    {
        string main = Text(MainKey, title);
        if (main.Length > 0)
        {
            yield return new KeyValuePair<FGuid, FAesKey>(new FGuid(), new FAesKey(main));
        }
        foreach ((string guid, string key) in Entries(DynamicKeys))
        {
            if (guid.Length > 0 && key.Length > 0)
            {
                yield return new KeyValuePair<FGuid, FAesKey>(new FGuid(guid), new FAesKey(key));
            }
        }
        foreach (KeyValuePair<FGuid, FAesKey> published in UnrealKeyring.Keys(title).Keys)
        {
            yield return published;
        }
    }

    public static FCustomVersionContainer CustomVersionContainer()
    {
        List<FCustomVersion> versions = new();
        foreach ((string guid, string version) in Entries(CustomVersions))
        {
            if (guid.Length > 0 && int.TryParse(version, out int number))
            {
                versions.Add(new FCustomVersion(new FGuid(guid), number));
            }
        }
        return new FCustomVersionContainer(versions);
    }

    public static Dictionary<string, bool> OptionOverrideTable()
    {
        Dictionary<string, bool> table = new(StringComparer.Ordinal);
        foreach ((string name, string value) in Entries(OptionOverrides))
        {
            if (name.Length > 0 && bool.TryParse(value, out bool enabled))
            {
                table[name] = enabled;
            }
        }
        return table;
    }

    public static Dictionary<string, KeyValuePair<string, string>> MapStructTypeTable()
    {
        Dictionary<string, KeyValuePair<string, string>> table = new(StringComparer.Ordinal);
        foreach ((string name, string value) in Entries(MapStructTypes))
        {
            int comma = value.IndexOf(',');
            if (name.Length > 0 && comma > 0)
            {
                table[name] = new KeyValuePair<string, string>(value[..comma].Trim(), value[(comma + 1)..].Trim());
            }
        }
        return table;
    }

    public static string[] ExtraDirectoryList()
    {
        List<string> directories = new();
        foreach ((string directory, _) in Entries(ExtraDirectories))
        {
            if (directory.Length > 0)
            {
                directories.Add(directory);
            }
        }
        return directories.ToArray();
    }

    /// <summary>
    /// A fingerprint of every option the provider is built from, so a session opened under one
    /// set of values is never mistaken for one opened under another.
    /// </summary>
    public static string Fingerprint() => Fingerprint(UnrealTitles.For(Session.GameRoot));

    public static string Fingerprint(UnrealTitle? title)
    {
        List<string> parts = new();
        foreach (Option option in Schema)
        {
            parts.Add(option.Name + ValueSeparator + Text(option.Name, title));
        }
        if (title is not null)
        {
            parts.Add(title.Product + ValueSeparator + UnrealKeyring.Keys(title).Token
                + ValueSeparator + UnrealKeyring.Mappings(title).Token);
        }
        return string.Join('\n', parts);
    }

    private static IEnumerable<string> EngineNames()
    {
        foreach (string name in Enum.GetNames<EGame>())
        {
            if (name.StartsWith("GAME_UE", StringComparison.Ordinal))
            {
                yield return name;
            }
        }
        foreach (string name in Enum.GetNames<EGame>())
        {
            if (!name.StartsWith("GAME_UE", StringComparison.Ordinal))
            {
                yield return name;
            }
        }
    }
}
