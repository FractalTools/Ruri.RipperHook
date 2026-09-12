namespace Ruri.RipperHook.Bridge;

/// <summary>
/// Which exported asset does a game container path name — answered from what the exporter
/// STATED as it wrote each collection, never from the file names it left behind.
///
/// <para>Export file names are lossy and must not be joined on. A filesystem refuses
/// characters a game's asset names use freely, clone/instance suffixes are stripped, names
/// are trimmed, and a name taken twice gets a uniquifying suffix
/// (<c>ExportCollection.GetUniqueFileName</c> / <c>FileSystem.GetUniqueName</c>) — so
/// "S_tree_hongshan_hardwood+1_001_02_COL1_UM01" is not reliably the stem of the file that
/// holds it. The two facts a scene actually needs — the asset's own name, and the
/// <c>{fileID, guid}</c> reference that points at it — are both stated by the exporter
/// itself while it still holds the live object, which is where a recorder must take
/// them.</para>
///
/// <para>A placement states the path the GAME files an asset under; the export states what
/// that asset became. The two disagree in three measured ways, so a path is tried in three
/// spellings, each strictly weaker than the last:</para>
/// <list type="number">
/// <item>the exact path — and <c>"a.fbx##sub"</c> names the sub-asset of <c>a.fbx</c> called
/// <c>sub</c>, which is how a placement points at one mesh of a multi-mesh model;</item>
/// <item>the path without its extension — a <c>".mesh"</c> container is written as
/// <c>".asset"</c>;</item>
/// <item>the leaf as the asset's own name — for a container whose export landed somewhere
/// else entirely (a package-rooted material against an Assets-rooted export path).</item>
/// </list>
///
/// <para>Ambiguity is never guessed at: a spelling naming more than one asset (after the
/// wanted class has narrowed it) resolves to nothing, because placing the WRONG asset is
/// worse than reporting the placement as unresolved.</para>
/// </summary>
public sealed class ExportedAssetIndex
{
    /// <summary>One exported asset, as the exporter itself stated it.</summary>
    /// <param name="ContainerPath">The asset's <c>OriginalPath</c> — where the game files it.</param>
    /// <param name="Name">The asset's own name, unsanitized.</param>
    /// <param name="ClassId">Unity class id, for narrowing an ambiguous path.</param>
    /// <param name="FileId">The <c>fileID</c> half of a reference to it.</param>
    /// <param name="Guid">The guid of the file it was written into.</param>
    /// <param name="ReferenceType">The <c>type:</c> half — AssetRipper's own AssetType.</param>
    public readonly record struct Entry(
        string ContainerPath, string Name, int ClassId, long FileId, string Guid, int ReferenceType);

    /// <summary>Any class — the default for a caller that knows the path is unambiguous.</summary>
    public const int AnyClass = 0;

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, List<int>> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byStem = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    public IReadOnlyList<Entry> Entries => _entries;

    public void Add(string? containerPath, string? name, int classId, long fileId, string guid, int referenceType)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return;
        }
        containerPath ??= string.Empty;
        name ??= string.Empty;
        int index = _entries.Count;
        _entries.Add(new Entry(containerPath, name, classId, fileId, guid, referenceType));

        if (containerPath.Length > 0)
        {
            string normalized = Normalize(containerPath);
            Bucket(_byPath, normalized).Add(index);
            string stem = StripExtension(normalized);
            if (!string.Equals(stem, normalized, StringComparison.Ordinal))
            {
                Bucket(_byStem, stem).Add(index);
            }
        }
        if (name.Length > 0)
        {
            Bucket(_byName, name).Add(index);
        }
    }

    /// <summary>
    /// The one asset this container path names, or null when it names none or more than one.
    /// <paramref name="classId"/> narrows an otherwise ambiguous path to the kind of thing
    /// the caller is placing (a Mesh, a Material); <see cref="AnyClass"/> does not narrow.
    /// </summary>
    public Entry? Resolve(string? path, int classId = AnyClass)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        int marker = path.IndexOf("##", StringComparison.Ordinal);
        if (marker >= 0)
        {
            // A sub-asset is named AND located, and it takes both: three different models in
            // one folder each ship a mesh called "Plane001", so the name alone is ambiguous
            // (measured -- it silently lost 91 placements) while the container it came out of
            // tells them apart. The name alone is still the last resort, for a sub-asset whose
            // container the export filed somewhere this side cannot predict.
            string container = Normalize(path[..marker]);
            string subName = path[(marker + 2)..];
            return Single(_byPath, container, classId, subName)
                ?? Single(_byStem, StripExtension(container), classId, subName)
                ?? Single(_byName, subName, classId);
        }
        string normalized = Normalize(path);
        return Single(_byPath, normalized, classId)
            ?? Single(_byStem, StripExtension(normalized), classId)
            ?? Single(_byName, LeafOf(StripExtension(normalized)), classId);
    }

    private Entry? Single(Dictionary<string, List<int>> index, string key, int classId, string? name = null)
    {
        if (key.Length == 0 || !index.TryGetValue(key, out List<int>? candidates))
        {
            return null;
        }
        Entry? found = null;
        foreach (int candidate in candidates)
        {
            Entry entry = _entries[candidate];
            if (classId != AnyClass && entry.ClassId != classId)
            {
                continue;
            }
            if (name is not null && !string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (found is not null)
            {
                return null;        // ambiguous — say nothing rather than place the wrong thing
            }
            found = entry;
        }
        return found;
    }

    private static List<int> Bucket(Dictionary<string, List<int>> index, string key)
    {
        if (!index.TryGetValue(key, out List<int>? bucket))
        {
            index[key] = bucket = new List<int>();
        }
        return bucket;
    }

    /// <summary>
    /// One spelling for a path stated by either side: forward slashes, no sub-asset marker,
    /// rooted at "Assets/" where there is one, lowercase. Mirrors the normalization the
    /// closure crossing already applies to the same two kinds of path.
    /// </summary>
    public static string Normalize(string path)
    {
        string slashed = path.Replace('\\', '/');
        int marker = slashed.IndexOf("##", StringComparison.Ordinal);
        string trimmed = marker >= 0 ? slashed[..marker] : slashed;
        int assets = trimmed.IndexOf("Assets/", StringComparison.OrdinalIgnoreCase);
        if (assets >= 0)
        {
            trimmed = trimmed[assets..];
        }
        return trimmed.ToLowerInvariant();
    }

    public static string StripExtension(string path)
    {
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        return dot > slash && dot >= 0 ? path[..dot] : path;
    }

    public static string LeafOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
