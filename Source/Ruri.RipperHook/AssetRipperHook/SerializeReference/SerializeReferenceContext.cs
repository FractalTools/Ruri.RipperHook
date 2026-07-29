using AssetRipper.Assets.Collections;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.IO.Endian;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;

namespace Ruri.RipperHook.AR;

/// <summary>
/// 读取 <c>[SerializeReference]</c> 需要的三样东西 —— 载荷类型树、程序集管理器、是否嵌套 ——
/// 上游 PR 是给 <c>SerializableStructure.Read</c> 加参数拿到的。
/// 本工程走 AOP 不能改签名,故用**线程内环境上下文**顺着调用栈传递,语义等价。
/// </summary>
public static class SerializeReferenceContext
{
    [ThreadStatic]
    private static IAssemblyManager? _assemblyManager;

    [ThreadStatic]
    private static IReadOnlyList<SerializedTypeReference>? _refTypes;

    [ThreadStatic]
    private static bool _readingReferencedObject;

    /// <summary>
    /// 文件自带的 <c>[SerializeReference]</c> 载荷类型树,按文件名索引
    /// (集合与其序列化文件同名)。
    /// </summary>
    private static readonly Dictionary<string, SerializedTypeReference[]> _refTypesByFile = new();

    public static IReadOnlyList<SerializedTypeReference> RefTypes => _refTypes ?? [];

    public static IAssemblyManager? AssemblyManager => _assemblyManager;

    public static bool ReadingReferencedObject => _readingReferencedObject;

    /// <summary>解析序列化文件时登记该文件的载荷类型树,按文件名索引。</summary>
    public static void RegisterRefTypes(string fileName, SerializedTypeReference[] refTypes)
    {
        if (refTypes.Length == 0 || string.IsNullOrEmpty(fileName))
        {
            return; // 绝大多数文件没有 [SerializeReference],不占表
        }
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

    /// <summary>在一次根资产读取期间提供上下文;离开即还原(嵌套安全)。</summary>
    public static Scope Enter(IReadOnlyList<SerializedTypeReference> refTypes, IAssemblyManager? assemblyManager)
    {
        Scope scope = new(_refTypes, _assemblyManager, _readingReferencedObject);
        _refTypes = refTypes;
        _assemblyManager = assemblyManager;
        _readingReferencedObject = false;
        return scope;
    }

    /// <summary>
    /// 读一条引用对象的载荷。嵌套结构里可能也带 registry 字段,但只有根资产写一份,
    /// 故这段期间把嵌套标志置位,让 <c>Read</c> 跳过它们。
    /// </summary>
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
