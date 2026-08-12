using AssetRipper.Assets.Collections;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.AR;

public static class SerializeReferenceContext
{
    [ThreadStatic]
    private static IAssemblyManager? _assemblyManager;

    [ThreadStatic]
    private static IReadOnlyList<SerializedTypeReference>? _refTypes;

    [ThreadStatic]
    private static bool _readingReferencedObject;

    private static readonly Dictionary<string, SerializedTypeReference[]> _refTypesByFile = new();

    public static IReadOnlyList<SerializedTypeReference> RefTypes => _refTypes ?? [];

    public static IAssemblyManager? AssemblyManager => _assemblyManager;

    public static bool ReadingReferencedObject => _readingReferencedObject;

    public static void RegisterRefTypes(string fileName, SerializedTypeReference[] refTypes)
    {
        if (refTypes.Length == 0 || string.IsNullOrEmpty(fileName))
        {
            return;        }
        lock (_refTypesByFile)
        {
            _refTypesByFile[fileName] = refTypes;
        }
    }

    public static IReadOnlyList<SerializedTypeReference> GetRefTypes(AssetCollection? collection)
    {
        if (collection is null || string.IsNullOrEmpty(collection.Name))
        {
            return [];
        }
        lock (_refTypesByFile)
        {
            return _refTypesByFile.TryGetValue(collection.Name, out SerializedTypeReference[]? refTypes) ? refTypes : [];
        }
    }

    public static Scope Enter(IReadOnlyList<SerializedTypeReference> refTypes, IAssemblyManager? assemblyManager)
    {
        Scope scope = new(_refTypes, _assemblyManager, _readingReferencedObject);
        _refTypes = refTypes;
        _assemblyManager = assemblyManager;
        _readingReferencedObject = false;
        return scope;
    }

    public static void ReadNested(
        SerializableStructure structure,
        ref EndianSpanReader reader,
        UnityVersion version,
        TransferInstructionFlags flags)
    {
        bool previous = _readingReferencedObject;
        _readingReferencedObject = true;
        try
        {
            structure.Read(ref reader, version, flags);
        }
        finally
        {
            _readingReferencedObject = previous;
        }
    }

    public readonly struct Scope : IDisposable
    {
        private readonly IReadOnlyList<SerializedTypeReference>? _previousRefTypes;
        private readonly IAssemblyManager? _previousAssemblyManager;
        private readonly bool _previousReadingReferencedObject;

        internal Scope(
            IReadOnlyList<SerializedTypeReference>? refTypes,
            IAssemblyManager? assemblyManager,
            bool readingReferencedObject)
        {
            _previousRefTypes = refTypes;
            _previousAssemblyManager = assemblyManager;
            _previousReadingReferencedObject = readingReferencedObject;
        }

        public void Dispose()
        {
            _refTypes = _previousRefTypes;
            _assemblyManager = _previousAssemblyManager;
            _readingReferencedObject = _previousReadingReferencedObject;
        }
    }
}
