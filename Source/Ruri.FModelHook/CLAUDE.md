## 实现状态:已完成(branch `feature/ue-unity-yaml-export`)

本文档的牛头蛇尾设计已 **完整实现并验证**(7 个 commit,headless CLI 自测试循环跑通,测试数据 `D:\Game\OniValleyDemo5.1`,UE5.1)。下面的设计正文保留作架构参考;实际落地与最初的若干推测有出入,**以本节为准**。

**结构铁律:所有 FModelHook 的 hook/特性都落在 `Source/Ruri.FModelHook/Game/SBUE/` 下**(与 `GlbSceneExport`/`ShaderDecompiler`/`Headless` 并列),命名空间 `Ruri.FModelHook.Game.SBUE.<Feature>`。UnityExport 即 `Game/SBUE/UnityExport/`。

**落地结构**(`Source/Ruri.FModelHook/Game/SBUE/UnityExport/`,命名空间 `Ruri.FModelHook.Game.SBUE.UnityExport.*`):
- `Engine/` — `IUnityObjectMapping` / `Mapping`(`Set`/`After` fluent builder)/ `MapperRegistry`(exact+基类链派发)/ `ConversionContext`(可重入、按源对象 dedup、cross-ref PPtr、group 导出)/ `UnityYamlExportSession`(回填阶段持 GameBundle + ProcessedAssetCollection,导出阶段把 bundle 交给 AR 的 `ExportHandler.Process + Export`,由 AR 自己的 `ProjectExporter` 按类型分派 Texture→PNG / Material→.mat / Mesh→.asset / Prefab→.prefab)/ `EnumMaps` / `StructCopy` 思路并入各 mapping / `VertexPacker`(无损 VertexData 打包)/ `MeshDataFactory`。
- `Mappings/` — `Texture / Material / StaticMesh / SkeletalMesh / Animation / World` + `UnityMappings.RegisterAll()`。
- `Hook/UE_UnityYamlExport_Hook.cs` — FModel GUI 右键「Export → Unity YAML」(detour `MainWindow.OnLoaded` + 全局 `ContextMenu.OpenedEvent`,0 改 FModel)。
- `UnityYamlExportRunner` — headless 驱动;`ConvertAndExport(provider, keys, outDir, ver, log, logErr)` 被 CLI 与 GUI 共用。

**CLI 自测试入口**:`Ruri.FModelHook.CLI.exe --export-unity --game-dir <Paks> --ue-version GAME_UE5_1 --mappings <usmap> --export-out <dir> [--package-filter tok,..] [--max-packages N] [--unity-version 2022.3.0f1]`。输出目录每次运行清空。验证产物:Texture2D(DXT/BC 直传)、Material(`m_TexEnvs/m_Floats/m_Colors`,texture PPtr 解析)、Mesh(无损 float VertexData + submesh + AABB)、SkeletalMesh(CompressedMesh 带骨权重 + bindpose + CRC32 骨名 + morph→BlendShape)、AnimationClip(legacy 曲线,骨骼路径绑定,ACL 由 CUE4Parse 解)、UWorld→`.prefab`(GameObject/Transform/MeshFilter/MeshRenderer,mesh+material 跨资源引用)。

**与原设计的关键修正(踩过的坑)**:
- **依赖不是 `Ruri.SourceGenerated.dll`,而是预构建的 AR DLL 闭包**(`AssetRipper/Source/0Bins/AssetRipper/<cfg>/`,经 `Source/AssetRipperRefs.props` 被 FModelHook **和** CLI 同时 `<Import>`)。原因:AR 是冻结区且其 `AssetRipper.SourceGenerated.Extensions` 的 Roslyn 源生成器 pin 在 Microsoft.CodeAnalysis 5.3,而 CLI 的 .NET SDK 只有 Roslyn 5.0 → **无法从命令行重编译冻结树**。改用预构建闭包 + RAR 传递解析,绝不重建冻结树。CLI 是 entry exe,**必须**自己也 import 这组引用,否则 `.deps.json` 缺 AR 程序集运行时 `FileNotFoundException`。
- **Mesh(ClassID 43)是 sealed group → 字段无 `_C43` 后缀**:`VertexData`/`IndexBuffer`/`SubMeshes`/`Shapes`/`BindPose`/`BoneNameHashes`/`Skin`/`LocalAABB` 都是裸名,name 用 `Name`(不是 `Name_C43`/`Name_R`)。Texture2D(`_C28`)、Material(`_C21`)才有后缀。
- **VertexData 打包用 AR 自己的 `VertexDataBlob.Create(MeshData,...)` 再 flush 到 `mesh.VertexData`**(它的 skin 通道是 `//todo` 空桩)→ 故 **skeletal mesh 改走 `mesh.FillWithCompressedMeshData`**(`compressedMesh.SetWeights` 是 2022.3 唯一带得动骨权重的路径;static mesh 保持无损 VertexData)。
- 几何/骨骼/动画的**解码全部复用 CUE4Parse-Conversion**(`UStaticMesh/USkeletalMesh.TryConvert`、`UAnimSequence.ConvertAnims`),免去手解 `FPackedNormal`/ACL。
- 坐标保持**原始 UE 轴**(与 mesh 一致);UE→Unity 基变换(Y/Z swap + 0.01)是后续统一线性变换。UV 已做 `1-V` 翻转。
- World Partition cell / OFPA 聚合(`WorldActorCollector`)与 shader 占位、texture 的 colorspace/wrap、CompleteImageSize 为已记录的后续增强点。

---

## 设计正文(原 TODO,保留作架构参考)

## UE 资源 → Unity YAML 导出 hook(牛头蛇尾架构)

```
[FModel / CUE4Parse]  →→→  [Ruri Mapper]  →→→  [AssetRipper]
   UE 二进制读取            path-based 数据搬运    Unity YAML 写出
   解压 / 反序列化           跨对象自由聚合         .asset / .meta / .unity
   = 牛头(已存在,不动)    = 蛇身(我们要做)     = 蛇尾(已存在,不动)
```

**核心原则:一切皆数据,平坦流。** FModel 已经把 cooked binary 全部解到 in-memory raw 形态(`FVector[]` / `byte[]` / `float[]` / Property dict,没有压缩、没有 lazy pointer、没有 binary blob)。我们要做的只是按 Unity class 的字段 layout **把数据回填进去**。两边对象结构对不对齐**完全不用在意** — 一个 Unity 字段的 source 可以是任意 C# 表达式,跨多少个 UE 对象都行。塞完后走 AR 正常的 YAML 导出路径,**不存在任何问题**。

下面所有路径/字段名/API 都是源码 grep 验证过的(三个 explore agent 报告),不是推测。

---

### Mapper engine — 核心 30 行

```csharp
public interface IUnityObjectMapping {
    Type SourceType { get; }
    IUnityObjectBase Apply(UObject source, ProcessedAssetCollection col);
}

public sealed class Mapping<TSrc, TDst> : IUnityObjectMapping
    where TSrc : UObject where TDst : IUnityObjectBase
{
    public Type SourceType => typeof(TSrc);
    private readonly Func<ProcessedAssetCollection, TDst> _create;
    private readonly List<Action<TSrc, TDst>> _setters = new();

    internal Mapping(Func<ProcessedAssetCollection, TDst> create) { _create = create; }

    public Mapping<TSrc, TDst> Set<TVal>(Expression<Func<TDst, TVal>> target, Func<TSrc, TVal> source) {
        var prop = ((MemberExpression)target.Body).Member as PropertyInfo
                ?? throw new ArgumentException("target must be a property");
        _setters.Add((s, d) => prop.SetValue(d, source(s)));
        return this;
    }

    public IUnityObjectBase Apply(UObject src, ProcessedAssetCollection col) {
        var dst = _create(col);
        foreach (var set in _setters) set((TSrc)src, dst);
        return dst;
    }
}

public static class MapperRegistry {
    private static readonly Dictionary<Type, IUnityObjectMapping> _map = new();
    public static Mapping<TSrc, TDst> Map<TSrc, TDst>(Func<ProcessedAssetCollection, TDst> create)
        where TSrc : UObject where TDst : IUnityObjectBase {
        var m = new Mapping<TSrc, TDst>(create);
        _map[typeof(TSrc)] = m;
        return m;
    }
    public static IUnityObjectBase? Convert(UObject src, ProcessedAssetCollection col)
        => _map.TryGetValue(src.GetType(), out var m) ? m.Apply(src, col) : null;
}
```

这就是全部框架。**~50 行**(加上 using / 命名空间)。剩下的全是 mapping 声明,不是框架代码。

---

### 字段映射怎么写(声明式)

```csharp
// Mappings/TextureMappings.cs — 所有 texture 相关 mapping 一个文件
public static class TextureMappings {
    public static void Register() {
        MapperRegistry.Map<UTexture2D, Texture2D>(col => col.CreateTexture2D())
            .Set(t => t.Name_C28,          s => new Utf8String(s.Name))
            .Set(t => t.Width_C28,         s => s.PlatformData.SizeX)
            .Set(t => t.Height_C28,        s => s.PlatformData.SizeY)
            .Set(t => t.Format_C28E,       s => EnumMaps.Pixel(s.Format))
            .Set(t => t.MipCount_C28,      s => s.PlatformData.Mips.Length)
            .Set(t => t.ImageData_C28,     s => ConcatAllMipBytes(s.PlatformData.Mips))
            .Set(t => t.IsReadable_C28,    s => false);
    }
}
```

Mesh + BlendShape **分散在多个 UE 对象**的真实例子(你点名的):

```csharp
// Mappings/MeshMappings.cs
MapperRegistry.Map<USkeletalMesh, Mesh>(col => col.CreateMesh())
    .Set(t => t.Name_C43,                s => new Utf8String(s.Name))
    .Set(t => t.VertexData_C43,          s => VertexPacker.PackSkeletal(s.LODModels[0]))
    .Set(t => t.IndexBuffer_C43,         s => IndexPacker.Pack(s.LODModels[0].Indices.Buffer))
    .Set(t => t.SubMeshes_C43,           s => s.LODModels[0].Sections.Select(BuildSubMesh).ToAccessList())
    .Set(t => t.BindPose_C43,            s => s.ReferenceSkeleton.FinalRefBonePose.Select(ConvertTransform).ToAccessList())
    .Set(t => t.BoneNameHashes_C43,      s => s.ReferenceSkeleton.FinalRefBoneInfo.Select(b => Crc32(b.Name.Text)).ToList())

    // ← 关键:morph target 数据在 s.MorphTargets[](独立 UObject 数组),Unity 端是 Mesh.Shapes 单字段
    //   source lambda 任意聚合,这种"跨对象"情况是天然支持的,不需要特殊设计
    .Set(t => t.Shapes_C43,              s => BlendShapeBuilder.FromMorphTargets(
                                              s.MorphTargets.Select(p => p.Load<UMorphTarget>()).Where(x => x != null).ToArray(),
                                              s.LODModels[0]));
```

**这就是"两边对象结构不对齐"的解法 — `source` lambda 想从哪个 UE 对象拿数据就从哪拿,框架不管。**

Material 同理 — `mat.GetParams(p, EMaterialFormat.AllLayers)` 已经把 UMaterial 的 scalar/vector/texture parameter dict 全部铺平,直接迭代填到 AR Material 的 `m_TexEnvs / m_Floats / m_Colors`:

```csharp
MapperRegistry.Map<UMaterialInterface, Material>(col => col.CreateMaterial())
    .Set(t => t.Name_C21,    s => new Utf8String(s.Name))
    .Set(t => t.SavedProperties_C21, s => BuildPropertyBlock(s));  // 内部调 GetParams + 拆 dict 进 m_TexEnvs/...
```

---

### 反射 / 表达式策略说明

- Mapper 用 `Expression<Func<TDst, TVal>>` 抽取 target `PropertyInfo`,然后 runtime `SetValue`。**不做 Expression.Compile**,因为单次 SetValue 反射开销可忽略(每个 asset 几十次),编译会引入复杂度 + 调试不便。
- `source` 直接是普通 lambda,**完全静态类型**,IDE 智能提示 + 编译期检查全部到位。重命名 UE 那边的字段会立刻爆红。
- 之前那版凭推测写过 "反射 + 同名字段自动复制"。**作废**。源码对比显示两边字段名匹配率几乎为 0(`PlatformData.SizeX` vs `Width_C28`,enum 值也不同),根本不存在"用一段 ReflectionCopier 就能完成大头"这种可能。映射全靠手写,Mapper 框架只是去掉了 boilerplate(对象构造 / 字段赋值循环),没有去掉"必须为每对类列出每个字段"的本质工作。

### 枚举 / 子 struct helper

```csharp
// EnumMaps.cs — 一处声明,所有 mapping 复用
public static class EnumMaps {
    public static TextureFormat Pixel(EPixelFormat f) => f switch {
        EPixelFormat.PF_DXT1 => TextureFormat.DXT1,
        EPixelFormat.PF_DXT5 => TextureFormat.DXT5,
        EPixelFormat.PF_BC7  => TextureFormat.BC7,
        EPixelFormat.PF_B8G8R8A8 => TextureFormat.BGRA32,
        // ... 一次性把 EPixelFormat 全列表列完,遗漏的抛 NotSupportedException 报清楚
        _ => throw new NotSupportedException($"Unmapped EPixelFormat: {f}"),
    };
    public static TextureWrapMode Wrap(TextureAddress a) => a switch {
        TextureAddress.TA_Wrap => TextureWrapMode.Repeat,
        TextureAddress.TA_Clamp => TextureWrapMode.Clamp,
        TextureAddress.TA_Mirror => TextureWrapMode.Mirror,
        _ => TextureWrapMode.Repeat,
    };
}

// StructCopy.cs — sub-struct X/Y/Z 同名的小 reflection helper(唯一允许反射的地方)
public static Vector3f ToUnity(this FVector v) => new Vector3f { X = v.X, Y = v.Y, Z = v.Z };
public static ColorRGBA32f ToUnity(this FLinearColor c) => new ColorRGBA32f { R = c.R, G = c.G, B = c.B, A = c.A };
public static Quaternionf ToUnity(this FQuat q) => new Quaternionf { X = q.X, Y = q.Y, Z = q.Z, W = q.W };
```

这些手写扩展方法 + enum switch 写一次永久复用,**比反射框架更简单可读**。

---

### AR 侧构造路径(全 public,无需 hook)

```csharp
var bundle = new GameBundle();
ProcessedAssetCollection col = bundle.AddNewProcessedCollection("Synthetic", UnityVersion.V_2022);
foreach (var lazy in pkg.ExportsLazy) {
    var unityObj = MapperRegistry.Convert(lazy.Value, col);   // null = 没有为这个 UE type 注册 mapping
    // unityObj 已经自动加入 col(因为 CreateXxx() 内部调的是 col.CreateAsset)
}
```

- `ProcessedAssetCollection.CreateAsset<T>(int classID, Func<AssetInfo, T> factory)` 是凭空构造 Unity 对象的唯一干净入口。`col.CreateTexture2D()` / `col.CreateMesh()` / `col.CreateMaterial()` 都是 `AssetCreator` 提供的扩展。
- `AddNewProcessedCollection` **必须传 UnityVersion**,否则版本派发落到 `Texture2D_3_5` 古董类,字段名都不一样。
- ImageData 直接 `tex.ImageData_C28 = bytes`,**不要碰 `StreamData_C28`**(那是 .resS 外链)。YAML serializer 自动转 hex blob。
- 跨 collection PPtr 要先 `targetCol.AddDependency(referencedCol)`。

### 工程写出(交给 AR 自己的 `ExportHandler`,绝不自己写编码/序列化)

```csharp
// 回填阶段:UnityYamlExportSession 把转换好的 IUnityObjectBase 全塞进一个
// ProcessedAssetCollection(在 GameBundle 里),cross-asset PPtr 走
// ConversionContext.Ptr() 已经建好。
// 导出阶段:直接把 GameBundle 喂给 AR 的 ExportHandler:
var settings = new FullConfiguration();             // ImageExportFormat 默认 Png
var assemblyManager = new BaseManager(_ => { });    // 我们的合成 bundle 没脚本,空 manager 即可
var gameData = new GameData(bundle, version, assemblyManager, null);
var handler = new ExportHandler(settings);
if (bundle.HasAnyAssetCollections())
    handler.Process(gameData);                      // SpriteProcessor 给每个 Texture2D 加 SpriteInformationObject MainAsset → 触发 TextureExportCollection → PNG
handler.Export(gameData, outputDir, LocalFileSystem.Instance);
```

为什么前一版 `DefaultYamlExporter` + 手写 `MinimalExportContainer` 是错的:
- Texture2D 走默认 YAML serializer → 输出 `.texture2D`(中间格式),不是 PNG。
- 真正的 PNG 路径是 `TextureAssetExporter`(AR 的 `Overrides.cs` 注册),且 **只在 `texture.MainAsset is SpriteInformationObject` 时**才能被 `TryCreateCollection` 选中——必须跑过 `SpriteProcessor`。
- `ProjectAssetContainer` 自带 cross-asset PPtr → `{fileID, guid, type}` 解析,自己写 stub 容器只够单 asset 用。

**关键铁律**:我们只做"回填"(UE 数据 → AR 对象 + PPtr cross-ref);**编码/PNG/序列化/路径布局** 全是 AR 自己的活,绝不在我们这边重复造轮子。新增 AR 程序集引用只在 `Source/AssetRipperRefs.props` 加 `HintPath`,绝不动 `AssetRipper/Source/`。

AR 的 `Logger` 是进程级 static 注册表——`ExportAll` 会在范围内 `Logger.Add(forwarder)` / `Logger.Remove(forwarder)` 把 Warning/Error 桥到我们的 logError,这样 "Can't export 'X' because resources file 'Y' missing" / "Unable to convert 'X' to bitmap" 这类 per-collection 失败诊断才会浮上来;否则它们沉默到 void、PNG 缺了都不知道原因。

AR `Import.dll` 的 `Logger` static ctor 强引用 `Cpp2IL.Core` + `LibCpp2IL`(订阅它们的日志事件),所以这两个 DLL 必须随 AR.Import 一起进入口 exe 目录——`AssetRipperRefs.props` 已 `<Reference HintPath>` 把它们带过去,千万别去掉。

---

### FModel GUI Hook(已验证不改 FModel 源码)

参考现有 `Game/SBUE/GlbSceneExport/UE_GlbSceneExport_Hook.cs` 的 hook 模式(同样 detour `MainWindow.OnLoaded` 注入菜单项 + 全局 `ContextMenu.OpenedEvent` 监听)。新建 `Game/<Game>/UnityExport/UE_UnityYamlExport_Hook.cs`:

- **Hook A**(注入菜单项)`[RetargetMethod(typeof(MainWindow), "OnLoaded", isBefore: true, isReturn: false)]` prefix-continue,在内部 `EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, OnContextMenuOpened)` 全局监听菜单打开,动态 `menu.Items.Add(new MenuItem { Header = "Export → Unity YAML", Click = OnClick })`。
- **Hook B**(点击处理)`OnClick` 里:
  ```csharp
  var vm = ApplicationService.ApplicationView.CUE4Parse;
  foreach (GameFileViewModel gvm in MainWindow.YesWeCats.AssetsListName.SelectedItems) {
      IPackage pkg = vm.Provider.LoadPackage(gvm.Asset);   // FModel 自动做完读取 + 解压 + 反序列化
      foreach (var lazy in pkg.ExportsLazy)
          MapperRegistry.Convert(lazy.Value, session.Collection);
  }
  session.ExportAll("C:/out/UnityProject");
  ```
- `ContextMenu` 在 FModel XAML 里是 `x:Shared="False"`,每次右键 new 一个实例。**必须用 `RegisterClassHandler` 全局监听**,不能 hold 单个 menu 引用。
- 估算 hook 代码量 60-80 行,纯 Ruri 侧,FModel 0 改动。

---

### 项目结构

```
Source/Ruri.FModelHook/
  UnityExport/                          # 跟 SBUE/ 并列(因为不只服务一个游戏)
    Engine/
      IUnityObjectMapping.cs            # ~30 行
      Mapping.cs                        # ~30 行 fluent builder
      MapperRegistry.cs                 # ~20 行
      UnityYamlExportSession.cs         # bundle + collection 生命周期 + 把 GameBundle 交给 AR ExportHandler 跑 Process+Export
      StructCopy.cs                     # FVector→Vector3f 等手写扩展
      EnumMaps.cs                       # EPixelFormat / TextureAddress / ... → Unity enum
    Mappings/
      TextureMappings.cs                # Register() 静态方法,被启动期调一次
      MaterialMappings.cs
      StaticMeshMappings.cs
      SkeletalMeshMappings.cs
      AnimationMappings.cs
      WorldMappings.cs                  # Scene / Prefab / Actor / Component(最大的一组)
    Hook/
      UE_UnityYamlExport_Hook.cs        # FModel GUI hook 入口
```

加新类型 = 加一行 `MapperRegistry.Map<X, Y>(…).Set(…)`,不动框架代码,不新建文件(同一 domain 文件追加即可)。

### 依赖前置(Phase 0)

`Ruri.FModelHook.csproj` 当前没有引用任何 AR 项目。要加:

```xml
<ProjectReference Include="..\..\AssetRipper\Source\AssetRipper.Assets\AssetRipper.Assets.csproj" />
<ProjectReference Include="..\..\AssetRipper\Source\AssetRipper.IO.Files\AssetRipper.IO.Files.csproj" />
<ProjectReference Include="..\..\AssetRipper\Source\AssetRipper.Export.UnityProjects\AssetRipper.Export.UnityProjects.csproj" />
<Reference Include="Ruri.SourceGenerated">
  <HintPath>..\Ruri.RipperHook\Libraries\Ruri.SourceGenerated.dll</HintPath>
</Reference>
```

Phase 0 完成的标志:`IUnityObjectBase` / `Texture2D.Create` / `YamlExporterBase` 三个 symbol 在 FModelHook 里能 resolve。

### 实施顺序

按"数据形状复杂度"递增,不按"用户优先级"。每一步打通后下一步只是加 mapping 声明,不动框架。

1. **Phase 0** ProjectReference + build 通过。
2. **Phase 1** Mapper engine + `MinimalExportContainer` + `UnityYamlExportSession` + FModel GUI hook + **`UTexture2D → Texture2D`** PoC(BC 直传,不 decode)。右键一个 .uasset → 输出 `.asset` + `.asset.meta`,end-to-end 走通。
3. **Phase 2** `UMaterialInterface → Material`(`mat.GetParams()` 拿 dict → 填 `m_TexEnvs/m_Floats/m_Colors`,shader 引用先全部指 `Hidden/InternalErrorShader`)。
4. **Phase 3** `UStaticMesh → Mesh`。需要写 `VertexPacker`(`FVector[]` → `VertexData.Data: byte[]`,按 channel descriptor pack,参考 AR `VertexDataBlob.cs` 反着写)。
5. **Phase 4** `USkeletalMesh → Mesh`,加 bone weights + bind pose + morph target → BlendShape **跨对象聚合**(本文档开头那个例子)。
6. **Phase 5** `UAnimSequence → AnimationClip`,uncooked `RawAnimationData` 路径优先(免 ACL codec)。
7. **Phase 6** `UWorld → Scene`,要构造 `SceneDefinition` + `SceneHierarchyObject` 聚合所有 `GameObject/Transform/Component`,actor/component 各自一个 mapping。最大的一组。

### 已知坑位

- AR `VertexData.Data` binary blob 的反向 packer **AR 没提供**,要自己按 `VertexData.Channels[]` 描述符 pack。读取代码在 `AssetRipper.SourceGenerated.Extensions/VertexDataBlob.cs`,反着写即可。
- `ContextMenu` 在 FModel XAML 是 `x:Shared="False"`,必须 `EventManager.RegisterClassHandler`,不能 hold 实例。
- Texture 像素是 GPU 编码字节(BC/DXT/PF_*),Unity 原生支持 → **直传**,只有目标格式不支持时才 decode 到 RGBA32(用 CUE4Parse `TextureDecoder`,不要自己写)。
- `AddNewProcessedCollection` 必须传 `UnityVersion`,否则版本派发落到 `Texture2D_3_5`(Unity 3.5)。
- AR YAML serializer 只接受 `IUnityObjectBase`。任何"先序列化成 JSON 中间格式再转 YAML"的想法都绕远路,**直接构造 AR 对象 + 调 YamlExporterBase 是唯一短路径**。
- `mat.GetParams()` 在某些游戏(自定义 Material expression graph)可能返回不完整数据。如果需要,fallback 到直接读 `mat.GetOrDefault<FStructFallback[]>("TextureParameterValues")` 等 tagged property。
- Mapping 声明里的 `source` lambda 异常会冒泡到外层 `MapperRegistry.Convert`,记得包 try/catch 把"哪个 asset / 哪个字段"信息加到错误日志(否则几千 asset 失败一个根本找不到现场)。
