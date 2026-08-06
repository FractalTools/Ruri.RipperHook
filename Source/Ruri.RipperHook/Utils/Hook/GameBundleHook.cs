using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.Import.AssetCreation;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files;
using AssetRipper.IO.Files.CompressedFiles;
using AssetRipper.IO.Files.ResourceFiles;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated;
using AssetRipper.SourceGenerated.Classes.ClassID_142;
using Ruri.RipperHook.Core;

namespace Ruri.RipperHook.HookUtils.GameBundleHook;

public class GameBundleHook : CommonHook, IHookModule
{
    private static readonly MethodInfo FromSerializedFile = typeof(SerializedAssetCollection)
        .GetMethod("FromSerializedFile", ReflectionExtensions.PrivateStaticBindFlag());

    public delegate void FilePreInitializeDelegate(GameBundle _this, IEnumerable<string> paths,
        List<FileBase> fileStack, FileSystem fileSystem, IDependencyProvider? dependencyProvider);

    /// <summary>
    /// Optional scan-mode filter consulted by the VFS chunk extractor: when set, a chunk's inner file is
    /// only extracted (and decrypted) if this returns <c>true</c> for its name. Building a CAB map only
    /// needs the AssetBundles that host SerializedFiles/CABs — skipping the bulk resource payloads
    /// (video, audio, tables, streaming data) the extractor would otherwise ChaCha-decrypt is what makes
    /// the map build fast. <c>null</c> (the default) extracts everything, so normal loading/export is
    /// completely unaffected. Set it for the duration of a scan, then reset to <c>null</c>.
    /// </summary>
    public static Func<string, bool>? ScanIncludeFile;

    /// <summary>
    /// Default <see cref="ScanIncludeFile"/> predicate for CAB-map scanning: keep AssetBundles and
    /// standalone Unity SerializedFiles, skip everything else (a pure resource payload never hosts a CAB,
    /// so there is nothing to index in it). Erring toward keeping is safe — an unwanted file just parses
    /// to a ResourceFile and adds no CAB — so the few standard SerializedFile name prefixes are included
    /// as a hedge in case a game ever ships assets unbundled.
    /// </summary>
    public static bool CabScanIncludeFile(string name)
    {
        ReadOnlySpan<char> n = name;
        if (n.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)) return true;
        if (HasBundlesSegment(n)) return true;
        int cut = n.LastIndexOfAny('/', '\\');
        ReadOnlySpan<char> leaf = cut >= 0 ? n[(cut + 1)..] : n;
        // .resS/.resource are raw payload siblings, not SerializedFiles -- keep them excluded even
        // though their base names start with "sharedassets"/"level".
        if (leaf.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return leaf.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("level", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith("resources.assets", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".assets", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("mainData", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A "bundles" path segment delimited by either separator on both sides — the
    /// zero-allocation equivalent of normalizing separators and searching "/bundles/". The filter
    /// runs once per inner file of every chunk (hundreds of thousands of calls per scan).</summary>
    private static bool HasBundlesSegment(ReadOnlySpan<char> name)
    {
        int from = 0;
        while (true)
        {
            int index = name[from..].IndexOf("bundles", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }
            int start = from + index;
            int end = start + "bundles".Length;
            if (start > 0 && name[start - 1] is '/' or '\\' && end < name.Length && name[end] is '/' or '\\')
            {
                return true;
            }
            from = start + 1;
        }
    }

    /// <summary>
    /// Optional load-mode filter consulted by the VFS chunk extractor on the NORMAL load path: when set,
    /// a chunk's inner bundle is only extracted (and decrypted) if this returns <c>true</c> for its name.
    /// This is what makes "load just pelica + its dependencies" possible — a single chunk can hold 161k
    /// bundles, so loading the whole chunk to reach the few hundred a target actually needs would exhaust
    /// memory. The CAB-map resolves a target to its exact dependency-closure CAB set; this filter then
    /// loads only those bundles out of the chunks that host them. <c>null</c> (the default) loads
    /// everything, so ordinary whole-game loading is completely unaffected. Set it for the duration of a
    /// scoped load, then reset to <c>null</c>.
    /// </summary>
    public static Func<string, bool>? LoadIncludeFile;

    // ── raw VFS file access + scene-placement discovery (non-Unity-CAB payloads) ──────────────────
    //
    // Every delegate below is deliberately typed in primitives/tuples only, NEVER a concrete game-hook
    // type (VirtualFileSystem, SceneChunkReader, Beyond.Gameplay.Streaming.*, ...) — this file lives
    // OUTSIDE AssetRipperGameHook/ and must keep compiling when that whole tree is stripped (the
    // "Pure" build: $(PureRelease)==true removes AssetRipperGameHook/**/*.cs entirely, see
    // Ruri.RipperHook.csproj). Same reasoning as ScanChunk/ScanChunkNames/ScanChunkFull above; the
    // actual implementation lives in AssetRipperGameHook/UnityHypergryph/EndField/Utils/StreamingScene/
    // EndfieldSceneBridge.cs and is wired in by the concrete game hook (e.g. EndField_1_2_4_Hook),
    // exactly like those three delegates are.

    /// <summary>One VFS-packed file's metadata, as a plain tuple (no concrete game-hook type):
    /// original name, its hash, its EVFSBlockType name, decrypted length, and which .chk hosts it.</summary>
    public delegate IEnumerable<(string FileName, long FileNameHash, string BlockType, long Length, string ChkPath)> EnumerateVfsFilesDelegate(string[] vfsRoots, string[]? blockTypeFilter);
    /// <summary>Set by a VFS game hook: enumerate every file across the given VFS roots (priority order,
    /// see <see cref="LoadIncludeFile"/>-style layered-root reasoning), of ANY block type -- not just
    /// Unity-CAB-shaped entries. <c>null</c> when no VFS hook is active.</summary>
    public static EnumerateVfsFilesDelegate? EnumerateVfsFiles;

    /// <summary>Set by a VFS game hook: extract + decrypt one VFS-packed file's raw bytes by its exact
    /// original name, trying the given roots in priority order with fallback (a hot-update overlay can
    /// list a file it never duplicated). <c>null</c> when no VFS hook is active.</summary>
    public delegate byte[] ExtractVfsFileDelegate(string[] vfsRoots, string fileName);
    public static ExtractVfsFileDelegate? ExtractVfsFile;

    /// <summary>Set by a VFS game hook: every distinct map name with streaming-chunk scene data across
    /// the given VFS roots. <c>null</c> when no VFS hook is active.</summary>
    public delegate string[] EnumerateSceneMapsDelegate(string[] vfsRoots);
    public static EnumerateSceneMapsDelegate? EnumerateSceneMaps;

    /// <summary>What one map ships, as the numbers a caller actually decides by: which scene states it
    /// has, and how much of it is cell-anchored chunks versus the map-wide/dynamic ones a window can only
    /// bound, not name. Manifest-only -- not one chunk byte is read. A summary rather than the file list
    /// on purpose: the one consumer showed two numbers, and a real map's list is 15k rows of interop for
    /// them.</summary>
    public delegate (int[] SceneStateIds, int AnchoredFiles, long AnchoredBytes, int FloatingFiles, long FloatingBytes) SceneChunkSummaryDelegate(string[] vfsRoots, string mapName);
    /// <summary>Set by a VFS game hook: one map's chunk inventory, summarized. <c>null</c> when no VFS
    /// hook is active.</summary>
    public static SceneChunkSummaryDelegate? SceneChunkSummary;

    /// <summary>One streaming window's IMPORTABLE content, reduced game-side: every kept placement (each
    /// one geometry with a verified transform, already the best available detail level per instance when
    /// lod0Only), the distinct container paths whose CABs an import needs (mesh + material, sorted), and
    /// the counts explaining what was left out -- Total raw rows, NoTransform (not geometry), LodFiltered
    /// (non-best detail siblings). A placement's SourceChunk is its chunk's full VFS name; its material
    /// paths are the entity's own (same FBPropertyAssetData list its mesh came from, AssetType==1 -- no
    /// naming-convention guess). Reduced here because a real window is 10^5 rows: filtering after the
    /// interop crossing pays for the crossing twice and re-pays the filter on every UI redraw.</summary>
    public delegate (int Total, int NoTransform, int LodFiltered, int DistinctAssets, string[] SeedPaths, (string AssetPath, long AssetHash, string EntityName, string SourceChunk, float Px, float Py, float Pz, float Qx, float Qy, float Qz, float Qw, float Sx, float Sy, float Sz, string[] MaterialAssetPaths)[] Placements) DiscoverScenePlacementsDelegate(string[] vfsRoots, string mapName, double minX, double minZ, double maxX, double maxZ, int[] sceneStateIds, bool lod0Only);
    /// <summary>Set by a VFS game hook: discover what one streaming window of a map places -- the world
    /// rect (<c>minX</c>, <c>minZ</c>)..(<c>maxX</c>, <c>maxZ</c>), gated by scene state, which is the
    /// shape the running game itself streams. An infinite rect is the whole map, never the default: a
    /// real one runs to thousands of chunks whose dependency closure no machine holds at once. World
    /// units rather than grid cells because that is what both ends of the game's own data speak: it
    /// streams by world position, and it publishes each named place as a world rect (see
    /// <see cref="SceneLandmarks"/>). <c>sceneStateIds</c> empty means every state the map ships.
    /// <c>null</c> when no VFS hook is active.</summary>
    public static DiscoverScenePlacementsDelegate? DiscoverScenePlacements;

    /// <summary>One named place the in-game map can show: its level id, whether it is a self-contained
    /// scene of its own (rather than a place inside a bigger streaming map), and the world rect the game
    /// gives it -- which is exactly the window <see cref="DiscoverScenePlacements"/> takes.</summary>
    public delegate IEnumerable<(string LevelId, bool IsSingleLevel, float MinX, float MinZ, float MaxX, float MaxZ)> SceneLandmarksDelegate(string[] vfsRoots);
    /// <summary>Set by a VFS game hook: every named place the game's own map UI lists, with its rect.
    /// This is what lets a caller ask for a place by name instead of guessing a grid coordinate, and what
    /// separates the maps that need windowing from the scenes that do not. <c>null</c> when no VFS hook
    /// is active.</summary>
    public static SceneLandmarksDelegate? SceneLandmarks;

    /// <summary>Set by a VFS game hook: binary/vtable-level schema-drift diagnostic -- one report line
    /// per FlatBuffers table type, flagging any type where the source data declares more fields
    /// than the currently-compiled bindings know how to read (see EndfieldSceneBridge.
    /// DiagnoseSchemaDrift's doc comment for why this is the only way to detect that gap).
    /// <c>null</c> when no VFS hook is active.</summary>
    public delegate string[] DiagnoseSchemaDriftDelegate(string[] vfsRoots, string mapName);
    public static DiagnoseSchemaDriftDelegate? DiagnoseSchemaDrift;

    /// <summary>Set by a VFS game hook: read one of the game's own self-describing data containers
    /// out of the VFS and project it into columns. <paramref name="flatColumnSpecs"/> is four strings
    /// per column -- name, dotted path, the container to resolve that value through (empty for no
    /// join), and the path taken inside the joined row -- the same flattened encoding the cabmap's
    /// filter rules already use, so no custom DTO crosses the reflection boundary.
    ///
    /// The result is a plain tuple of buffers, like every other delegate here: <c>Kinds[i]</c> is
    /// "text" (Blobs[i] is UTF-8 and Offsets[i] is RowCount+1 little-endian int32s), "int" (RowCount
    /// little-endian int64s, Offsets[i] empty) or "real" (RowCount little-endian float64s). Column 0
    /// is the row's own key. <c>null</c> when no VFS hook is active.</summary>
    /// <summary><paramref name="distinctBy"/> (empty for none) collapses the result to one row per
    /// distinct value of that column, keeping whichever row has a non-empty
    /// <paramref name="preferNonEmpty"/>. Handle names the projected table for
    /// <see cref="SearchDataTable"/>.</summary>
    public delegate (string Handle, string Name, int RowCount, string[] Columns, string[] Kinds, byte[][] Blobs, byte[][] Offsets)
        QueryDataTableDelegate(string[] vfsRoots, string containerFile, string[] flatColumnSpecs,
            string distinctBy, string preferNonEmpty, CancellationToken cancellation);
    public static QueryDataTableDelegate? QueryDataTable;

    /// <summary>Set by a VFS game hook: row ids of an already-projected table whose text matches the
    /// query, on the same vectorized engine the cabmap browser searches with -- one folded blob per
    /// column, swept in parallel, no per-row strings. <c>null</c> when no VFS hook is active.</summary>
    public delegate int[] SearchDataTableDelegate(string handle, string query);
    public static SearchDataTableDelegate? SearchDataTable;

    /// <summary>Set by a VFS game hook: the part prefab names one npc template is assembled from,
    /// plus the character it corresponds to (empty when generic), how many detail levels it has,
    /// and its facial-morph avatar name. A generic npc ships no model prefab of its own -- the
    /// bundle index cannot answer this, only the game's own per-template manifest can.
    /// <c>null</c> when no VFS hook is active.</summary>
    public delegate (string[] Parts, string CharacterId, int LodCount, string FacialMorph, string AvatarTemplet)
        NpcPrefabPartsDelegate(string[] vfsRoots, string templateId);
    public static NpcPrefabPartsDelegate? NpcPrefabParts;

    /// <summary>Set by a VFS game hook: every npc template the game ships an assembled model for.
    /// <c>null</c> when no VFS hook is active.</summary>
    public delegate string[] NpcPrefabManifestDelegate(string[] vfsRoots);
    public static NpcPrefabManifestDelegate? NpcPrefabManifest;

    /// <summary>Set by a VFS game hook: pull each character's model prefab name and its
    /// expression-table tag out of the exported character data assets. Four strings per
    /// character (id, model, tag, asset name). Takes already-exported text so the loading stays
    /// on the generic side and only the field names live with the game. <c>null</c> when no VFS
    /// hook is active.</summary>
    public delegate string[] CharacterModelsDelegate(string[] assetTexts);
    public static CharacterModelsDelegate? CharacterModels;

    /// <summary>
    /// Set by a VFS game hook: given an on-disk path, decrypt + parse JUST the SerializedFile metadata of
    /// every CAB-hosting bundle the path contains, and return one tuple per SerializedFile — releasing each
    /// bundle's bytes as it goes (bounded memory) and extracting in parallel. This is the fast path the CAB
    /// map builder prefers; <c>null</c> when no VFS hook is active (the builder then falls back to a generic
    /// per-file scheme read).
    /// </summary>
    public delegate List<(string Cab, List<string> Deps, List<int> ClassIds)> ScanChunkDelegate(string path);
    public static ScanChunkDelegate? ScanChunk;

    /// <summary>
    /// Project one SerializedFile's metadata into a CAB-map tuple: its CAB name (its fixed name, or
    /// <paramref name="fallbackName"/> when unnamed), the distinct dependency CAB names it references, and
    /// the distinct ClassIDs from its type table (a MonoBehaviour's negative script-type index maps to 114).
    /// Reads metadata only — never touches a single object's data.
    /// </summary>
    public static (string Cab, List<string> Deps, List<int> ClassIds) ReadSerializedMetadata(SerializedFile sf, string fallbackName)
    {
        string cab = string.IsNullOrWhiteSpace(sf.NameFixed) ? fallbackName : sf.NameFixed;
        List<string> deps = new();
        foreach (FileIdentifier dependency in sf.Dependencies)
        {
            string name = dependency.GetFilePath();
            if (!string.IsNullOrWhiteSpace(name) && !deps.Contains(name, StringComparer.OrdinalIgnoreCase))
                deps.Add(name);
        }
        HashSet<int> classIds = new();
        foreach (SerializedType type in sf.Types)
            classIds.Add(type.TypeID < 0 ? 114 : type.TypeID);
        return (cab, deps, classIds.ToList());
    }

    // ── name scan (CAB → its AssetBundle Container addressable paths) ─────────────────────────────
    //
    // The CAB map keys everything by content hash; the human-readable names ("…/pelica/…") live only
    // inside each bundle's AssetBundle (ClassID 142) object, in its Container — the addressable path of
    // every asset the bundle hosts. A name scan reads ONLY that one object per CAB (skipping the heavy
    // Mesh/AnimationClip/Texture payloads) so it stays metadata-cheap and bounded-memory, then pairs the
    // names with the CAB map's dependency graph to expand a name match to its full dependency closure.

    /// <summary>
    /// Default version a name scan reads the AssetBundle object at when a SerializedFile's version is
    /// stripped. Set by the active game hook (EndField uses its custom experimental class version);
    /// resolving the source-generated AssetBundle layout needs a concrete version.
    /// </summary>
    public static UnityVersion NameScanVersion;

    /// <summary>
    /// Set by a VFS game hook: decrypt + parse each CAB-hosting bundle of an on-disk path and return one
    /// tuple per SerializedFile of (CAB name, its AssetBundle Container addressable paths). Bounded-memory
    /// and parallel like <see cref="ScanChunk"/>; <c>null</c> when no VFS hook is active.
    /// </summary>
    public delegate List<(string Cab, string FileName, List<string> Paths)> ScanChunkNamesDelegate(string path);
    public static ScanChunkNamesDelegate? ScanChunkNames;

    /// <summary>
    /// Set by a VFS game hook: the COMBINED scan — one decrypt+parse pass per bundle that projects both the
    /// CAB-map metadata (deps, ClassIDs) and the readable names (chunk-entry file name, AssetBundle
    /// Container addressable paths). One pass over the game builds the self-contained map;
    /// <c>null</c> when no VFS hook is active (the builder then falls back to a generic per-file read).
    /// </summary>
    public delegate List<(string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths)> ScanChunkFullDelegate(string path);
    public static ScanChunkFullDelegate? ScanChunkFull;

    /// <summary>
    /// Project one SerializedFile to the combined CAB-map row: metadata (deps + ClassIDs, see
    /// <see cref="ReadSerializedMetadata"/>) plus the readable names (chunk-entry file name + Container
    /// addressable paths, see <see cref="ReadContainerNames"/>) — one parse, both projections.
    /// </summary>
    public static (string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths) ReadFullMetadata(SerializedFile sf, string fallbackName)
    {
        (string cab, List<string> deps, List<int> classIds) = ReadSerializedMetadata(sf, fallbackName);
        (_, _, List<string> paths) = ReadContainerNames(sf, fallbackName);
        return (cab, fallbackName, deps, classIds, paths);
    }

    /// <summary>Separator between a host file's CAB name and an asset PathID in a per-asset virtual
    /// row's key ("sharedassets0.assets::1234"). "::" never occurs in a real CAB/file name.</summary>
    public const string AssetRowSeparator = "::";

    /// <summary>
    /// <see cref="ReadFullMetadata"/> plus per-ASSET expansion for non-bundled files: when a
    /// SerializedFile has NO AssetBundle Container (a plain player build's level0/
    /// sharedassetsN.assets/resources.assets — nothing bundled, so no addressable path exists
    /// anywhere), every named asset it hosts (<see cref="HarvestAssetNames"/>) becomes its OWN
    /// browsable row: key "&lt;hostCab&gt;::&lt;pathID&gt;", name = the asset's actual m_Name, class =
    /// its actual ClassID, and a single dependency edge back to the host file -- so the dependency
    /// closure of an asset row resolves to exactly the host file + its real transitive deps, and a
    /// browser shows one row per Mesh/AnimationClip/Texture/Material instead of one opaque row per
    /// 10k-asset container file. The host row itself is kept (whole-file import stays possible) with
    /// no name list of its own -- the names live on the asset rows.
    /// </summary>
    public static List<(string Cab, string FileName, List<string> Deps, List<int> ClassIds, List<string> Paths)> ReadFullMetadataRows(SerializedFile sf, string fallbackName)
    {
        (string cab, string fileName, List<string> deps, List<int> classIds, List<string> paths) =
            ReadFullMetadata(sf, fallbackName);
        List<(string, string, List<string>, List<int>, List<string>)> rows = new()
        {
            (cab, fileName, deps, classIds, paths),
        };
        if (paths.Count > 0)
        {
            return rows; // bundled: the container paths already name everything
        }
        foreach ((long pathId, int classId, string name) in HarvestAssetNames(sf))
        {
            rows.Add(($"{cab}{AssetRowSeparator}{pathId}", fileName,
                new List<string> { cab }, new List<int> { classId }, new List<string> { name }));
        }
        return rows;
    }

    // ── named-asset harvest (non-bundled SerializedFiles: level0/sharedassets/resources.assets) ────
    //
    // A plain player build has no AssetBundle objects at all, so ReadContainerNames yields nothing and
    // the whole file would surface as one opaque hash-named row. But every named asset's serialized data
    // *carries its own m_Name* — for the NamedObject family it is literally the first field (aligned
    // length-prefixed UTF-8), and for the two important exceptions (GameObject, MonoBehaviour) it sits at
    // a layout offset derivable from the file's format generation + Unity version. Reading it needs no
    // TypeTree and no asset materialization: ObjectInfo.ObjectData already exposes each object's raw byte
    // window, so the harvest is one strictly-validated string peek per object — O(object count), zero
    // per-object allocation beyond the accepted names.

    /// <summary>
    /// Every readable asset in a SerializedFile, from the assets' own m_Name fields: (PathID, ClassID,
    /// Name) per named object. Strict validation (sane length, printable strict UTF-8) makes the
    /// leading-string peek self-rejecting for nameless classes (components/managers start with a PPtr
    /// whose fileID bytes fail the length check), so no per-class whitelist is needed beyond the
    /// GameObject/MonoBehaviour layout special cases. PathID is what gives each harvested asset a
    /// browsable identity of its own (see CabMap's per-asset virtual rows for non-bundled files).
    /// </summary>
    public static List<(long PathId, int ClassId, string Name)> HarvestAssetNames(SerializedFile sf)
    {
        List<(long, int, string)> assets = new();
        bool bigEndian = sf.EndianType == EndianType.BigEndian;
        int pathIdSize = ObjectInfo.IsLongID(sf.Generation) ? 8 : 4;
        int pptrSize = sizeof(int) + pathIdSize;
        // GameObject.m_Component entries: 5.5+ is a bare PPtr; earlier carries a leading class-id int32.
        int componentEntrySize = sf.Version.GreaterThanOrEquals(5, 5) ? pptrSize : sizeof(int) + pptrSize;

        foreach (ObjectInfo objectInfo in sf.Objects)
        {
            ReadOnlySpan<byte> data = objectInfo.ObjectData;
            int classId = objectInfo.TypeID < 0 ? (int)ClassIDType.MonoBehaviour : objectInfo.TypeID;
            int offset;
            switch (classId)
            {
                case (int)ClassIDType.GameObject:
                {
                    // m_Component array, m_Layer, m_Name.
                    if (data.Length < sizeof(int))
                    {
                        continue;
                    }
                    int count = ReadInt32(data, 0, bigEndian);
                    long afterArray = sizeof(int) + (long)count * componentEntrySize;
                    if (count < 0 || count > 0x10000 || afterArray + sizeof(int) >= data.Length)
                    {
                        continue;
                    }
                    offset = (int)afterArray + sizeof(int); // + m_Layer
                    break;
                }
                case (int)ClassIDType.MonoBehaviour:
                    // m_GameObject PPtr, m_Enabled u8 + 3 align, m_Script PPtr, m_Name.
                    offset = pptrSize + sizeof(int) + pptrSize;
                    break;
                default:
                    offset = 0; // NamedObject family: m_Name is the first field; others self-reject below
                    break;
            }

            string? name = TryReadAlignedString(data, offset, bigEndian);
            if (name is not null)
            {
                assets.Add((objectInfo.FileID, classId, name));
            }
        }
        return assets;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian) => bigEndian
        ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[offset..])
        : System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);

    /// <summary>
    /// Read a Unity aligned length-prefixed string at <paramref name="offset"/>, returning <c>null</c>
    /// unless it validates as a plausible asset name: length 1..255 and in-bounds, no ASCII control
    /// characters, and strictly valid UTF-8 (any malformed byte sequence rejects the whole candidate) —
    /// what makes the offset-0 peek safe to attempt on every class without a whitelist.
    /// </summary>
    private static string? TryReadAlignedString(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        if (offset < 0 || offset + sizeof(int) > data.Length)
        {
            return null;
        }
        int length = ReadInt32(data, offset, bigEndian);
        if (length <= 0 || length > 255 || offset + sizeof(int) + length > data.Length)
        {
            return null;
        }
        ReadOnlySpan<byte> bytes = data.Slice(offset + sizeof(int), length);
        foreach (byte b in bytes)
        {
            if (b < 0x20 || b == 0x7F)
            {
                return null;
            }
        }
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>
    /// A factory that materialises ONLY the AssetBundle (142) object of a collection, returning <c>null</c>
    /// for every other class so <see cref="SerializedAssetCollection"/>.FromSerializedFile skips the heavy
    /// Mesh/AnimationClip/Texture payload reads. The AssetBundle Container already lists every asset's
    /// readable addressable path, so this one small object is all a name scan needs.
    /// </summary>
    private sealed class AssetBundleOnlyFactory : AssetFactoryBase
    {
        private readonly GameAssetFactory _inner;

        public AssetBundleOnlyFactory(IAssemblyManager assemblyManager)
        {
            _inner = new GameAssetFactory(assemblyManager);
        }

        public override IUnityObjectBase? ReadAsset(AssetInfo assetInfo, ReadOnlyArraySegment<byte> assetData, SerializedType? assetType)
        {
            return assetInfo.ClassID == (int)ClassIDType.AssetBundle
                ? _inner.ReadAsset(assetInfo, assetData, assetType)
                : null;
        }
    }

    private static readonly IAssemblyManager NameScanAssemblyManager = new BaseManager(static _ => { });
    private static readonly AssetBundleOnlyFactory NameScanFactory = new(NameScanAssemblyManager);

    /// <summary>
    /// Read one SerializedFile's AssetBundle Container — the readable addressable paths of every asset it
    /// hosts (e.g. <c>assets/beyond/arts/entity/actor/.../pelica/...</c>) — by materialising only the
    /// AssetBundle object. Metadata-cheap: skips all heavy payload objects. Returns the CAB name (for
    /// resolving back through the CAB map's dependency graph), the chunk-entry file name that hosts it
    /// (e.g. <c>Data/Bundles/Windows/main/&lt;hash&gt;.ab</c> — the key a scoped load must filter by, since
    /// it differs from the inner CAB name), and the distinct container paths.
    /// </summary>
    public static (string Cab, string FileName, List<string> Paths) ReadContainerNames(SerializedFile sf, string fallbackName)
    {
        string cab = string.IsNullOrWhiteSpace(sf.NameFixed) ? fallbackName : sf.NameFixed;
        List<string> paths = new();
        try
        {
            AssetCollection collection = (AssetCollection)FromSerializedFile.Invoke(null, new object[] { new GameBundle(), sf, NameScanFactory, NameScanVersion })!;
            foreach (IUnityObjectBase asset in collection)
            {
                if (asset is not IAssetBundle assetBundle)
                {
                    continue;
                }
                var container = assetBundle.Container;
                int count = container.Count;
                for (int i = 0; i < count; i++)
                {
                    string key = container.GetKey(i).String;
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        paths.Add(key);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Verbose(LogCategory.Import, $"[NameScan] '{cab}': {exception.GetType().Name}: {exception.Message}");
        }
        return (cab, fallbackName, paths);
    }

    // Static callback used by the hooked method
    public static FilePreInitializeDelegate CustomFilePreInitialize;

    // Instance callback stored until activation
    private readonly FilePreInitializeDelegate _moduleCallback;

    public GameBundleHook(FilePreInitializeDelegate callback)
    {
        _moduleCallback = callback;
    }

    public void OnApply()
    {
        CustomFilePreInitialize = _moduleCallback;
    }

    [RetargetMethod(typeof(GameBundle), "InitializeFromPaths")]
    public void InitializeFromPaths(IEnumerable<string> paths, AssetFactoryBase assetFactory, FileSystem fileSystem, IGameInitializer? initializer)
    {
        var _this = (object)this as GameBundle;

        _this.ResourceProvider = initializer?.ResourceProvider;
        var fileStack = new List<FileBase>();
        UnityVersion defaultVersion = initializer is null ? default : initializer.DefaultVersion;

        var phase = System.Diagnostics.Stopwatch.StartNew();
        CustomFilePreInitialize(_this, paths, fileStack, fileSystem, initializer?.DependencyProvider);
        long preInitializeMs = phase.ElapsedMilliseconds;

        phase.Restart();
        // Deserializing a container's assets touches only that container plus the stateless
        // static factory (GameAssetFactory is pure static readers; typetree-backed assets never
        // consult shared assembly state), so every FileContainer's bundle is precomputed across
        // all cores here. The stack replay below then runs in the exact LIFO order the
        // sequential loop used, so GameBundle child order -- and everything keyed off it --
        // is identical.
        SerializedBundle?[] preBuiltBundles = new SerializedBundle?[fileStack.Count];
        Parallel.For(0, fileStack.Count, index =>
        {
            if (fileStack[index] is FileContainer container)
            {
                preBuiltBundles[index] = SerializedBundle.FromFileContainer(container, assetFactory, defaultVersion);
            }
        });

        for (int index = fileStack.Count - 1; index >= 0; index--)
        {
            switch (fileStack[index])
            {
                case SerializedFile serializedFile:
                    FromSerializedFile.Invoke(null, new object[] { _this, serializedFile, assetFactory, defaultVersion });
                    break;
                case FileContainer:
                    _this.AddBundle(preBuiltBundles[index]!);
                    break;
                case ResourceFile resourceFile:
                    _this.AddResource(resourceFile);
                    break;
                case FailedFile failedFile:
                    _this.AddFailed(failedFile);
                    break;
            }
        }
        fileStack.Clear();
        Logger.Info(LogCategory.Import,
            $"[GameBundle] preInit={preInitializeMs}ms assetRead={phase.ElapsedMilliseconds}ms");
    }

    private static FileBase RemoveLastItem(List<FileBase> list)
    {
        var index = list.Count - 1;
        var file = list[index];
        list.RemoveAt(index);
        return file;
    }

    // Static Helper (unchanged)
    public static List<FileBase> LoadFilesAndDependencies(byte[] buffer, string path, string name, IDependencyProvider? dependencyProvider)
    {
        List<FileBase> files = new();
        HashSet<string> serializedFileNames = new();

        var file = SchemeReader.ReadFile(buffer, path, name);

        try
        {
            file?.ReadContentsRecursively();
        }
        catch (Exception ex)
        {
            file = new FailedFile()
            {
                Name = name,
                FilePath = path,
                StackTrace = ex.ToString(),
            };
        }

        while (file is CompressedFile compressedFile)
            file = compressedFile.UncompressedFile;

        if (file is ResourceFile || file is FailedFile)
        {
            files.Add(file);
        }
        else if (file is SerializedFile serializedFile)
        {
            files.Add(file);
            serializedFileNames.Add(serializedFile.NameFixed);
        }
        else if (file is FileContainer container)
        {
            files.Add(file);
            foreach (var serializedFileInContainer in container.FetchSerializedFiles())
                serializedFileNames.Add(serializedFileInContainer.NameFixed);
        }

        for (var i = 0; i < files.Count; i++)
        {
            var file1 = files[i];
            if (file1 is SerializedFile serializedFile)
                LoadDependencies(serializedFile, files, serializedFileNames, dependencyProvider);
            else if (file1 is FileContainer container)
                foreach (var serializedFileInContainer in container.FetchSerializedFiles())
                    LoadDependencies(serializedFileInContainer, files, serializedFileNames, dependencyProvider);
        }

        return files;
    }

    // LoadFilesAndDependencies runs on parallel per-chunk workers (see the EndField
    // GameBundlePreInitialize); everything it touches is per-call state EXCEPT the shared
    // dependency provider, whose thread safety is not its contract -- serialize just that.
    private static readonly object _dependencyProviderLock = new();

    private static void LoadDependencies(SerializedFile serializedFile, List<FileBase> files, HashSet<string> serializedFileNames, IDependencyProvider? dependencyProvider)
    {
        foreach (var fileIdentifier in serializedFile.Dependencies)
        {
            var name = fileIdentifier.GetFilePath();
            if (!serializedFileNames.Add(name) || dependencyProvider is null)
                continue;
            FileBase? dependency;
            lock (_dependencyProviderLock)
            {
                dependency = dependencyProvider.FindDependency(fileIdentifier);
            }
            if (dependency is not null)
                files.Add(dependency);
        }
    }
}