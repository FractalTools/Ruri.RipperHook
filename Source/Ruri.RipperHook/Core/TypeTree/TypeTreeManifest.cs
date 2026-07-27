using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ruri.RipperHook.Core.TypeTree;

/// <summary>
/// The version index that lets a tpk be addressed by free-form version strings.
///
/// The tpk container is not modified to carry this. A tpk already defines two blob kinds for exactly
/// this purpose -- <c>Collection</c> (named blobs) and <c>Json</c> ("custom json data") -- so the
/// shipped file is a stock <c>TpkCollectionBlob</c> holding one stock <c>TpkTypeTreeBlob</c> per
/// lineage plus this manifest. Every blob written is a stock blob; the format is untouched.
///
/// Inside a lineage's blob, versions are plain ordinals (<c>0.0.0</c>, <c>0.0.1</c>, ...) with no
/// meaning of their own -- the manifest is what maps a real version string to its ordinal. That is
/// what removes the old constraints: a build is no longer packed into <c>UnityVersion.Build</c>
/// (a <see cref="ushort"/>, so <c>1.4.4</c> -&gt; <c>1404</c> overflowed past five digits and
/// collided -- <c>1.0.14</c> and <c>10.1.4</c> both packed to <c>1014</c>).
///
/// Each lineage is a complete, self-contained chain: its engine's snapshots up to the game's base
/// version, then the game's own snapshots. A class the game never redefines resolves inside that one
/// chain, so a lookup never leaves its lineage and never falls back to another game's definitions.
/// </summary>
public sealed class TypeTreeManifest
{
    /// <summary>Blob name the manifest is stored under inside the collection.</summary>
    public const string BlobName = "ruri.versions";

    /// <summary>Bumped when the manifest shape changes so a stale archive fails loudly.</summary>
    public const int CurrentFormatVersion = 1;

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    [JsonPropertyName("lineages")]
    public List<LineageEntry> Lineages { get; set; } = new();

    public sealed class LineageEntry
    {
        /// <summary>Lineage key, e.g. <c>EndField</c>. Also the name of its blob in the collection.</summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        /// <summary>
        /// Version entries in chain order; the index is the ordinal used inside the lineage's blob.
        /// The leading entries are the engine snapshots the game builds on.
        /// </summary>
        [JsonPropertyName("versions")]
        public List<VersionEntry> Versions { get; set; } = new();
    }

    public sealed class VersionEntry
    {
        /// <summary>The version as the game names it, e.g. <c>1.4.4</c>. Free-form.</summary>
        [JsonPropertyName("v")]
        public string Key { get; set; } = "";

        /// <summary>
        /// The Unity version this snapshot actually is, taken from the dump itself -- for a fork that
        /// is the fork's own build (EndField reports <c>2021.3.34f5</c>, not the <c>f1</c> it was
        /// derived from). It is what picks which stock AssetRipper class the loader instantiates, so
        /// it is read rather than guessed at the call site.
        /// </summary>
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = "";
    }

    private Dictionary<string, LineageEntry>? byKey;

    public LineageEntry? Find(string lineage)
    {
        byKey ??= BuildIndex();
        return byKey.TryGetValue(lineage, out LineageEntry? entry) ? entry : null;
    }

    /// <summary>
    /// The ordinal for a version string, or -1 when this lineage does not declare it. A miss is an
    /// error for the caller to report -- there is deliberately no nearest-match behaviour, because
    /// silently reading a neighbouring build's layout is how a desynchronized stream looks like
    /// success.
    /// </summary>
    public int GetOrdinal(string lineage, string version)
    {
        LineageEntry? entry = Find(lineage);
        if (entry is null)
        {
            return -1;
        }

        for (int i = 0; i < entry.Versions.Count; i++)
        {
            if (string.Equals(entry.Versions[i].Key, version, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The Unity version a snapshot reports, or <see langword="null"/> when it is unknown.</summary>
    public string? GetEngine(string lineage, string version)
    {
        LineageEntry? entry = Find(lineage);
        if (entry is null)
        {
            return null;
        }

        foreach (VersionEntry candidate in entry.Versions)
        {
            if (string.Equals(candidate.Key, version, StringComparison.Ordinal))
            {
                return candidate.Engine;
            }
        }
        return null;
    }

    private Dictionary<string, LineageEntry> BuildIndex()
    {
        Dictionary<string, LineageEntry> index = new(Lineages.Count, StringComparer.Ordinal);
        foreach (LineageEntry entry in Lineages)
        {
            index[entry.Key] = entry;
        }
        return index;
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static TypeTreeManifest FromJson(string json)
    {
        TypeTreeManifest manifest = JsonSerializer.Deserialize<TypeTreeManifest>(json, SerializerOptions)
            ?? throw new InvalidOperationException("[TypeTreeManifest] Empty manifest.");

        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"[TypeTreeManifest] Manifest format {manifest.FormatVersion} != expected {CurrentFormatVersion}; rebuild the tpk with Ruri.Tpk.");
        }

        return manifest;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
