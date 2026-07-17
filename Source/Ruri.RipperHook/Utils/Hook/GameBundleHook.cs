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
        string n = name.Replace('\\', '/');
        if (n.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Contains("/bundles/", StringComparison.OrdinalIgnoreCase)) return true;
        string leaf = n.Contains('/') ? n[(n.LastIndexOf('/') + 1)..] : n;
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

    /// <summary>One mesh-bearing scene-entity placement, as a plain tuple (no concrete game-hook type):
    /// resolved asset path (empty if the hash didn't resolve), asset hash, entity name, source chunk file
    /// name, whether a usable transform source was found, the transform itself
    /// (identity/zero when HasTransform is false -- treat as "don't place", not "place at the origin"),
    /// and the entity's own resolved material asset path(s) (empty array if it has none or none resolved
    /// -- same FBPropertyAssetData property list AssetPath comes from, just AssetType==1 instead of ==2,
    /// so no separate lookup mechanism, no naming-convention guess).</summary>
    public delegate IEnumerable<(string AssetPath, long AssetHash, string EntityName, string SourceChunk, bool HasTransform, float Px, float Py, float Pz, float Qx, float Qy, float Qz, float Qw, float Sx, float Sy, float Sz, string[] MaterialAssetPaths)> DiscoverScenePlacementsDelegate(string[] vfsRoots, string mapName);
    /// <summary>Set by a VFS game hook: discover every mesh-bearing entity placement for a map's
    /// streaming chunks. <c>null</c> when no VFS hook is active.</summary>
    public static DiscoverScenePlacementsDelegate? DiscoverScenePlacements;

    /// <summary>Set by a VFS game hook: binary/vtable-level schema-drift diagnostic -- one report line
    /// per FlatBuffers table type, flagging any type where the source data declares more fields
    /// than the currently-compiled bindings know how to read (see EndfieldSceneBridge.
    /// DiagnoseSchemaDrift's doc comment for why this is the only way to detect that gap).
    /// <c>null</c> when no VFS hook is active.</summary>
    public delegate string[] DiagnoseSchemaDriftDelegate(string[] vfsRoots, string mapName);
    public static DiagnoseSchemaDriftDelegate? DiagnoseSchemaDrift;

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
    /// Container addressable paths). One pass over the game builds the RCM3 map that needs no sidecar;
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

        CustomFilePreInitialize(_this, paths, fileStack, fileSystem, initializer?.DependencyProvider);

        while (fileStack.Count > 0)
        {
            switch (RemoveLastItem(fileStack))
            {
                case SerializedFile serializedFile:
                    FromSerializedFile.Invoke(null, new object[] { _this, serializedFile, assetFactory, defaultVersion });
                    break;
                case FileContainer container:
                    var serializedBundle = SerializedBundle.FromFileContainer(container, assetFactory, defaultVersion);
                    _this.AddBundle(serializedBundle);
                    break;
                case ResourceFile resourceFile:
                    _this.AddResource(resourceFile);
                    break;
                case FailedFile failedFile:
                    _this.AddFailed(failedFile);
                    break;
            }
        }
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

    private static void LoadDependencies(SerializedFile serializedFile, List<FileBase> files, HashSet<string> serializedFileNames, IDependencyProvider? dependencyProvider)
    {
        foreach (var fileIdentifier in serializedFile.Dependencies)
        {
            var name = fileIdentifier.GetFilePath();
            if (serializedFileNames.Add(name) && dependencyProvider?.FindDependency(fileIdentifier) is { } dependency)
                files.Add(dependency);
        }
    }
}