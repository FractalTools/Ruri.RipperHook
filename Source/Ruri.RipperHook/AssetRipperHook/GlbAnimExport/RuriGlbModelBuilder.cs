using System.Numerics;
using AssetRipper.Assets;
using AssetRipper.Import.Logging;
using AssetRipper.Numerics;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_91;
using AssetRipper.SourceGenerated.Classes.ClassID_95;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Quaternionf;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Vector3f;
using AssetRipper.SourceGenerated.Subclasses.QuaternionCurve;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using AssetRipper.SourceGenerated.Subclasses.Vector3Curve;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;

namespace Ruri.RipperHook.GlbAnimExport;

/// <summary>
/// 给 AR 的 GLB 导出补上它本来没有的两块:蒙皮(SkinnedMeshRenderer→带骨架的蒙皮网格)和动画
/// (AnimationClip→glTF 动画轨道)。数据全部取自 AR 已还原的纯 Unity 资产(MeshData、已解码的
/// PositionCurves/RotationCurves/...),坐标系转换 1:1 复刻 AR 内部的 GlbCoordinateConversion(它是 internal,
/// 源码引不到,这里照抄)。由 <see cref="AR_GlbAnimExport_Hook"/> 替换 GlbModelExporter.ExportModel 调用。
/// </summary>
public static class RuriGlbModelBuilder
{
    public static SceneBuilder Build(IEnumerable<IUnityObjectBase> assets, bool isScene)
    {
        SceneBuilder scene = new();
        Dictionary<ITransform, NodeBuilder> nodeByTransform = new();
        Dictionary<string, NodeBuilder> nodeByPath = new(StringComparer.Ordinal);
        List<(ISkinnedMeshRenderer Renderer, NodeBuilder Node)> skinnedRenderers = new();
        List<IAnimator> animators = new();
        HashSet<IUnityObjectBase> exported = new();

        foreach (IUnityObjectBase asset in assets)
        {
            if (asset is not (IGameObject or IComponent) || exported.Contains(asset))
            {
                continue;
            }

            IGameObject root = GetRoot(asset);
            ITransform rootTransform = root.GetTransform();
            AddTransformTree(scene, parentNode: null, rootTransform, parentPath: null, isScene,
                nodeByTransform, nodeByPath, skinnedRenderers, animators);

            foreach (IUnityObjectBase exportedAsset in root.FetchHierarchy())
            {
                exported.Add(exportedAsset);
            }
        }

        // 节点树建好后再建蒙皮网格(需要所有骨骼节点都已存在)。
        foreach ((ISkinnedMeshRenderer renderer, NodeBuilder node) in skinnedRenderers)
        {
            try
            {
                BuildSkinnedMesh(scene, renderer, node, nodeByTransform);
            }
            catch (Exception ex)
            {
                Logger.Warning(LogCategory.Export, $"[GLB] skinned mesh failed for '{renderer.GetBestName()}': {ex.Message}");
            }
        }

        // 动画:收集 Animator 控制器里的 clip,逐条采样写成 glTF 动画。
        foreach (IAnimationClip clip in CollectClips(animators))
        {
            try
            {
                AddAnimation(clip, nodeByPath);
            }
            catch (Exception ex)
            {
                Logger.Warning(LogCategory.Export, $"[GLB] animation failed for '{clip.GetBestName()}': {ex.Message}");
            }
        }

        return scene;
    }

    private static void AddTransformTree(
        SceneBuilder scene,
        NodeBuilder? parentNode,
        ITransform transform,
        string? parentPath,
        bool isScene,
        Dictionary<ITransform, NodeBuilder> nodeByTransform,
        Dictionary<string, NodeBuilder> nodeByPath,
        List<(ISkinnedMeshRenderer, NodeBuilder)> skinnedRenderers,
        List<IAnimator> animators)
    {
        IGameObject? gameObject = transform.GameObject_C4P;
        if (gameObject is null)
        {
            return;
        }

        NodeBuilder node = parentNode is null ? new NodeBuilder(gameObject.Name) : parentNode.CreateNode(gameObject.Name);

        // 根节点(prefab,非场景)按 AR 的约定保持单位变换;其余节点写本地 TRS。
        if (parentNode is not null || isScene)
        {
            node.LocalTransform = new AffineTransform(
                transform.LocalScale_C4.CastToStruct(),
                ToGltfQuaternion(transform.LocalRotation_C4.CastToStruct()),
                ToGltfVector3(transform.LocalPosition_C4.CastToStruct()));
        }

        if (parentNode is null)
        {
            scene.AddNode(node);
        }

        nodeByTransform[transform] = node;
        // 动画路径:相对 root,root 自身为 ""(Unity 动画曲线路径约定)。
        string path = parentPath is null ? string.Empty : (parentPath.Length == 0 ? gameObject.Name : parentPath + "/" + gameObject.Name);
        nodeByPath[path] = node;

        if (gameObject.TryGetComponent(out ISkinnedMeshRenderer? skinnedRenderer))
        {
            skinnedRenderers.Add((skinnedRenderer, node));
        }
        if (gameObject.TryGetComponent(out IAnimator? animator))
        {
            animators.Add(animator);
        }

        foreach (ITransform child in transform.Children_C4P.WhereNotNull())
        {
            AddTransformTree(scene, node, child, path, isScene, nodeByTransform, nodeByPath, skinnedRenderers, animators);
        }
    }

    private static void BuildSkinnedMesh(SceneBuilder scene, ISkinnedMeshRenderer renderer, NodeBuilder rendererNode, Dictionary<ITransform, NodeBuilder> nodeByTransform)
    {
        IMesh? mesh = renderer.MeshP;
        if (mesh is null || !MeshData.TryMakeFromMesh(mesh, out MeshData meshData))
        {
            return;
        }

        // 关节:SkinnedMeshRenderer.Bones 的顺序就是顶点蒙皮索引(BoneWeight4.Index)引用的顺序。
        PPtrAccessListShim bones = new(renderer);
        int boneCount = bones.Count;
        NodeBuilder[] jointNodes = new NodeBuilder[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            ITransform? boneTransform = bones[i];
            if (boneTransform is not null && nodeByTransform.TryGetValue(boneTransform, out NodeBuilder? jn))
            {
                jointNodes[i] = jn;
            }
            else
            {
                // 兜底:缺失的骨骼挂到 renderer 节点,避免索引错位。
                jointNodes[i] = rendererNode;
            }
        }

        if (boneCount == 0)
        {
            return;
        }

        IMeshBuilder<MaterialBuilder> meshBuilder = BuildMeshBuilder(mesh, meshData);
        // 用关节节点的世界变换(= 绑定姿势层级)反推逆绑定矩阵,避开 Unity bindpose 的矩阵约定坑。
        scene.AddSkinnedMesh(meshBuilder, Matrix4x4.Identity, jointNodes);
    }

    private static IMeshBuilder<MaterialBuilder> BuildMeshBuilder(IMesh mesh, MeshData meshData)
    {
        MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> builder = new(mesh.GetBestName());
        MaterialBuilder material = new("default");
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexJoints4> primitive = builder.UsePrimitive(material);

        bool is16 = mesh.Is16BitIndices();
        uint[] indexBuffer = meshData.ProcessedIndexBuffer;
        for (int s = 0; s < mesh.SubMeshes.Count; s++)
        {
            ISubMesh subMesh = mesh.SubMeshes[s];
            if (subMesh.Has_Topology() && subMesh.TopologyE != MeshTopology.Triangles)
            {
                continue; // TODO: line/strip/quad topologies
            }

            uint firstIndex = subMesh.FirstByte / (is16 ? 2u : 4u);
            uint indexCount = subMesh.IndexCount;
            for (uint i = 0; i + 2 < indexCount; i += 3)
            {
                // glTF 顶点缠绕与 Unity 相反:逆序加三角形。
                primitive.AddTriangle(
                    GetVertex(meshData, indexBuffer[firstIndex + i + 2]),
                    GetVertex(meshData, indexBuffer[firstIndex + i + 1]),
                    GetVertex(meshData, indexBuffer[firstIndex + i]));
            }
        }

        return builder;
    }

    private static VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> GetVertex(MeshData meshData, uint index)
    {
        Vector3 position = ToGltfVector3(meshData.TryGetVertexAtIndex(index));
        Vector3 normal = meshData.HasNormals ? ToGltfVector3(Vector3.Normalize(meshData.TryGetNormalAtIndex(index))) : Vector3.UnitY;
        Vector2 uv = meshData.UVCount > 0 ? meshData.TryGetUV0AtIndex(index) : Vector2.Zero;

        VertexJoints4 skin;
        if (meshData.HasSkin)
        {
            BoneWeight4 bw = meshData.TryGetSkinAtIndex(index);
            SparseWeight8 sparse = SparseWeight8.Create(
                new Vector4(bw.Index0, bw.Index1, bw.Index2, bw.Index3),
                new Vector4(bw.Weight0, bw.Weight1, bw.Weight2, bw.Weight3));
            skin = new VertexJoints4(sparse);
        }
        else
        {
            skin = default;
        }

        return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(
            new VertexPositionNormal(position, normal),
            new VertexTexture1(uv),
            skin);
    }

    private static List<IAnimationClip> CollectClips(List<IAnimator> animators)
    {
        List<IAnimationClip> clips = new();
        HashSet<IAnimationClip> seen = new();
        foreach (IAnimator animator in animators)
        {
            if (GetController(animator) is IAnimatorController controller)
            {
                foreach (IAnimationClip? clip in controller.AnimationClipsP)
                {
                    if (clip is not null && seen.Add(clip))
                    {
                        clips.Add(clip);
                    }
                }
            }
        }
        return clips;
    }

    // Animator 的控制器指针按 Unity 版本分了三种(4 / 4.3 / 5+),取存在的那个。
    // 返回 IUnityObjectBase:IAnimatorController(91)与 IRuntimeAnimatorController(93)在 AR 里非父子,
    // 调用方用运行时 is 判定具体类型。
    private static IUnityObjectBase? GetController(IAnimator animator)
    {
        if (animator.Has_Controller_PPtr_RuntimeAnimatorController_5())
        {
            return animator.Controller_PPtr_RuntimeAnimatorController_5P;
        }
        if (animator.Has_Controller_PPtr_RuntimeAnimatorController_4_3())
        {
            return animator.Controller_PPtr_RuntimeAnimatorController_4_3P;
        }
        if (animator.Has_Controller_PPtr_AnimatorController_4())
        {
            return animator.Controller_PPtr_AnimatorController_4P;
        }
        return null;
    }

    private static void AddAnimation(IAnimationClip clip, Dictionary<string, NodeBuilder> nodeByPath)
    {
        string name = clip.GetBestName();

        for (int i = 0; i < clip.PositionCurves_C74.Count; i++)
        {
            IVector3Curve curve = clip.PositionCurves_C74[i];
            if (!nodeByPath.TryGetValue(curve.Path.String, out NodeBuilder? node))
            {
                continue;
            }
            SharpGLTF.Animations.CurveBuilder<Vector3> track = node.UseTranslation(name);
            for (int k = 0; k < curve.Curve.Curve.Count; k++)
            {
                IKeyframe_Vector3f key = curve.Curve.Curve[k];
                track.SetPoint(key.Time, ToGltfVector3(key.Value.CastToStruct()));
            }
        }

        for (int i = 0; i < clip.RotationCurves_C74.Count; i++)
        {
            IQuaternionCurve curve = clip.RotationCurves_C74[i];
            if (!nodeByPath.TryGetValue(curve.Path.String, out NodeBuilder? node))
            {
                continue;
            }
            SharpGLTF.Animations.CurveBuilder<Quaternion> track = node.UseRotation(name);
            for (int k = 0; k < curve.Curve.Curve.Count; k++)
            {
                IKeyframe_Quaternionf key = curve.Curve.Curve[k];
                track.SetPoint(key.Time, ToGltfQuaternion(key.Value.CastToStruct()));
            }
        }

        for (int i = 0; i < clip.ScaleCurves_C74.Count; i++)
        {
            IVector3Curve curve = clip.ScaleCurves_C74[i];
            if (!nodeByPath.TryGetValue(curve.Path.String, out NodeBuilder? node))
            {
                continue;
            }
            SharpGLTF.Animations.CurveBuilder<Vector3> track = node.UseScale(name);
            for (int k = 0; k < curve.Curve.Curve.Count; k++)
            {
                IKeyframe_Vector3f key = curve.Curve.Curve[k];
                // 缩放在两套坐标系下相同,不翻转。
                track.SetPoint(key.Time, key.Value.CastToStruct());
            }
        }

        for (int i = 0; i < clip.EulerCurves_C74.Count; i++)
        {
            IVector3Curve curve = clip.EulerCurves_C74[i];
            if (!nodeByPath.TryGetValue(curve.Path.String, out NodeBuilder? node))
            {
                continue;
            }
            SharpGLTF.Animations.CurveBuilder<Quaternion> track = node.UseRotation(name);
            for (int k = 0; k < curve.Curve.Curve.Count; k++)
            {
                IKeyframe_Vector3f key = curve.Curve.Curve[k];
                Quaternion unityQuat = EulerToQuaternion(key.Value.CastToStruct());
                track.SetPoint(key.Time, ToGltfQuaternion(unityQuat));
            }
        }
    }

    private static IGameObject GetRoot(IUnityObjectBase asset)
    {
        return asset switch
        {
            IGameObject gameObject => gameObject.GetRoot(),
            IComponent component => component.GameObject_C2P!.GetRoot(),
            _ => throw new InvalidOperationException(),
        };
    }

    // —— 坐标系转换:1:1 复刻 AssetRipper.Export.Modules.Models.GlbCoordinateConversion(internal,源码不可引)——
    private static readonly Vector3 CoordinateSpaceConversionScale = new(-1, 1, 1);

    private static Vector3 ToGltfVector3(Vector3 unityVector) => unityVector * CoordinateSpaceConversionScale;

    private static Quaternion ToGltfQuaternion(Quaternion unityQuaternion)
    {
        Vector3 fromAxis = new(unityQuaternion.X, unityQuaternion.Y, unityQuaternion.Z);
        Vector3 toAxis = -1f * fromAxis * CoordinateSpaceConversionScale;
        return new Quaternion(toAxis.X, toAxis.Y, toAxis.Z, unityQuaternion.W);
    }

    // Unity 欧拉(度,ZXY 内旋)→四元数。
    private static Quaternion EulerToQuaternion(Vector3 eulerDegrees)
    {
        const float deg2Rad = MathF.PI / 180f;
        float x = eulerDegrees.X * deg2Rad;
        float y = eulerDegrees.Y * deg2Rad;
        float z = eulerDegrees.Z * deg2Rad;
        Quaternion qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, x);
        Quaternion qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, y);
        Quaternion qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z);
        return qy * qx * qz;
    }

    // SkinnedMeshRenderer.BonesP 的轻量索引包装(避免到处写 PPtrAccessList 泛型)。
    private readonly struct PPtrAccessListShim
    {
        private readonly ISkinnedMeshRenderer _renderer;
        public PPtrAccessListShim(ISkinnedMeshRenderer renderer) => _renderer = renderer;
        public int Count => _renderer.BonesP.Count;
        public ITransform? this[int index] => _renderer.BonesP[index];
    }
}
