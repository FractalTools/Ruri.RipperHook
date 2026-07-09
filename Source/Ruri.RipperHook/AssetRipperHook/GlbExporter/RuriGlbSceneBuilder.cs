using System.Numerics;
using System.Text.Json.Nodes;
using AssetRipper.Assets;
using AssetRipper.Export.Modules.Models;
using AssetRipper.Import.Logging;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Classes.ClassID_91;
using AssetRipper.SourceGenerated.Classes.ClassID_95;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.BlendShapeData;
using AssetRipper.SourceGenerated.Subclasses.BlendShapeVertex;
using AssetRipper.SourceGenerated.Subclasses.FloatCurve;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Quaternionf;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Vector3f;
using AssetRipper.SourceGenerated.Subclasses.MeshBlendShape;
using AssetRipper.SourceGenerated.Subclasses.MeshBlendShapeChannel;
using AssetRipper.SourceGenerated.Subclasses.QuaternionCurve;
using AssetRipper.SourceGenerated.Subclasses.Skeleton;
using AssetRipper.SourceGenerated.Subclasses.SkeletonPose;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using AssetRipper.SourceGenerated.Subclasses.Vector3Curve;
using AssetRipper.SourceGenerated.Subclasses.Vector4Float;
using AssetRipper.SourceGenerated.Subclasses.Xform;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;

namespace Ruri.RipperHook.GlbExporter;

/// <summary>
/// Builds a complete glTF scene from a prefab/scene hierarchy: node tree, skinned + static meshes
/// (full vertex data via AR's GlbSubMeshBuilder), materials with textures, morph targets, and
/// animations. Generic transform curves bind by the paths AR's PathChecksumCache already recovered
/// (Avatar TOS + hierarchy CRC32); humanoid muscle curves are baked to bone rotations by
/// <see cref="HumanoidClipBaker"/>. Bones the prefab stripped but the Avatar still knows are
/// synthesized from the avatar skeleton's default pose so every animation-dependent bone exists.
/// </summary>
public static class RuriGlbSceneBuilder
{
    public static SceneBuilder Build(IEnumerable<IUnityObjectBase> assets, bool isScene)
    {
        SceneBuilder scene = new();
        BuildContext context = new(scene, isScene);

        foreach (IUnityObjectBase asset in assets)
        {
            if (asset is not (IGameObject or IComponent) || context.Exported.Contains(asset))
            {
                continue;
            }

            IGameObject root = GetRoot(asset);
            AddTransformTree(context, parentNode: null, root.GetTransform(), parentPath: null);

            foreach (IUnityObjectBase exportedAsset in root.FetchHierarchy())
            {
                context.Exported.Add(exportedAsset);
            }
        }

        // Avatar 骨架补全要在网格/动画之前:被 strip 的骨骼节点先长出来。
        foreach (AnimatorEntry animator in context.Animators)
        {
            SynthesizeMissingAvatarBones(context, animator);
        }

        foreach (SkinnedRendererEntry entry in context.SkinnedRenderers)
        {
            try
            {
                BuildSkinnedMesh(context, entry);
            }
            catch (Exception ex)
            {
                Logger.Warning(LogCategory.Export, $"[GLB] skinned mesh failed for '{entry.Renderer.GetBestName()}': {ex.Message}");
            }
        }

        foreach ((IMeshFilter meshFilter, IRenderer renderer, NodeBuilder node) in context.StaticRenderers)
        {
            try
            {
                BuildStaticMesh(context, meshFilter, renderer, node);
            }
            catch (Exception ex)
            {
                Logger.Warning(LogCategory.Export, $"[GLB] static mesh failed for '{renderer.GetBestName()}': {ex.Message}");
            }
        }

        AddAnimations(context);
        return scene;
    }

    // ---- node tree -------------------------------------------------------------------------

    private static void AddTransformTree(BuildContext context, NodeBuilder? parentNode, ITransform transform, string? parentPath)
    {
        IGameObject? gameObject = transform.GameObject_C4P;
        if (gameObject is null)
        {
            return;
        }

        // SharpGLTF 的 IsValidArmature 要求骨架树内节点名唯一,游戏层级常见重名(attach/effect 等)——
        // 建树时确定性去重;蒙皮按 joint 索引、动画绑定按 Unity 路径字典,改名无损。
        string nodeName = context.UniqueNodeName(gameObject.Name);
        NodeBuilder node = parentNode is null ? new NodeBuilder(nodeName) : parentNode.CreateNode(nodeName);
        Vector3 localPosition = transform.LocalPosition_C4.CastToStruct();
        Quaternion localRotation = transform.LocalRotation_C4.CastToStruct();
        Vector3 localScale = transform.LocalScale_C4.CastToStruct();

        // 根节点(prefab,非场景)按 AR 的约定保持单位变换;其余节点写本地 TRS。
        if (parentNode is not null || context.IsScene)
        {
            node.LocalTransform = new AffineTransform(
                localScale,
                GlbCoordinateConversion.ToGltfQuaternionConvert(localRotation),
                GlbCoordinateConversion.ToGltfVector3Convert(localPosition));
        }

        if (parentNode is null)
        {
            context.Scene.AddNode(node);
        }

        // 动画路径:相对 root,root 自身为 ""(Unity 动画曲线路径约定)。
        string path = parentPath is null ? string.Empty : (parentPath.Length == 0 ? gameObject.Name : parentPath + "/" + gameObject.Name);
        context.NodeByTransform[transform] = node;
        context.NodeByPath[path] = node;
        context.RestByPath[path] = new UnityLocalTransform(localPosition, localRotation, localScale);

        if (gameObject.TryGetComponent(out ISkinnedMeshRenderer? skinnedRenderer))
        {
            context.SkinnedRenderers.Add(new SkinnedRendererEntry(skinnedRenderer, node, path));
        }
        if (gameObject.TryGetComponent(out IMeshFilter? meshFilter)
            && gameObject.TryGetComponent(out IRenderer? meshRenderer)
            && meshRenderer is not ISkinnedMeshRenderer)
        {
            context.StaticRenderers.Add((meshFilter, meshRenderer, node));
        }
        if (gameObject.TryGetComponent(out IAnimator? animator))
        {
            context.Animators.Add(new AnimatorEntry(animator, node, path));
        }

        foreach (ITransform child in transform.Children_C4P.WhereNotNull())
        {
            AddTransformTree(context, node, child, path);
        }
    }

    /// <summary>
    /// Grow avatar-skeleton bones the prefab hierarchy does not carry (stripped/optimized rigs):
    /// every TOS path missing from the node tree is created under its skeleton parent with the
    /// avatar DefaultPose local TRS — the same restoration VibeStudio's
    /// ModelConverter.DeoptimizeTransformHierarchy (ModelConverter.cs:1338) performs.
    /// </summary>
    private static void SynthesizeMissingAvatarBones(BuildContext context, AnimatorEntry animatorEntry)
    {
        IAvatar? avatar = animatorEntry.Animator.AvatarP;
        if (avatar is null || avatar.Avatar.AvatarSkeleton is null || avatar.Avatar.DefaultPose is null)
        {
            return;
        }

        ISkeleton skeleton = avatar.Avatar.AvatarSkeleton.Data;
        ISkeletonPose defaultPose = avatar.Avatar.DefaultPose.Data;
        int created = 0;

        // 骨架数组父在前子在后;顺序遍历保证父节点先于子节点被补出来。
        for (int i = 0; i < skeleton.Node.Count && i < skeleton.ID.Count; i++)
        {
            if (!avatar.TOS.TryGetValue(skeleton.ID[i], out Utf8String? tosPath) || tosPath.IsEmpty)
            {
                continue; // 根节点(路径 "")就是 animator 节点本身,永远已存在。
            }
            string path = ResolvePath(animatorEntry.Path, tosPath.String);
            if (context.NodeByPath.ContainsKey(path))
            {
                continue;
            }

            int parentId = skeleton.Node[i].ParentId;
            string parentPath = animatorEntry.Path;
            if (parentId >= 0 && parentId < skeleton.ID.Count
                && avatar.TOS.TryGetValue(skeleton.ID[parentId], out Utf8String? parentTos) && !parentTos.IsEmpty)
            {
                parentPath = ResolvePath(animatorEntry.Path, parentTos.String);
            }
            if (!context.NodeByPath.TryGetValue(parentPath, out NodeBuilder? parentNode))
            {
                continue; // 父链断裂(TOS 不完整),跳过该骨骼。
            }

            int slash = path.LastIndexOf('/');
            string name = context.UniqueNodeName(slash < 0 ? path : path[(slash + 1)..]);
            Vector3 position = Vector3.Zero;
            Quaternion rotation = Quaternion.Identity;
            Vector3 scale = Vector3.One;
            if (i < defaultPose.X.Count)
            {
                IXform xform = defaultPose.X[i];
                position = ToVector3(xform);
                rotation = new Quaternion(xform.Q.X, xform.Q.Y, xform.Q.Z, xform.Q.W);
                scale = ToScale(xform);
            }

            NodeBuilder node = parentNode.CreateNode(name);
            node.LocalTransform = new AffineTransform(
                scale,
                GlbCoordinateConversion.ToGltfQuaternionConvert(rotation),
                GlbCoordinateConversion.ToGltfVector3Convert(position));
            context.NodeByPath[path] = node;
            context.RestByPath[path] = new UnityLocalTransform(position, rotation, scale);
            created++;
        }

        if (created > 0)
        {
            Logger.Info(LogCategory.Export, $"[GLB] synthesized {created} avatar bones missing from the hierarchy of '{animatorEntry.Animator.GetBestName()}'");
        }
    }

    // ---- meshes ----------------------------------------------------------------------------

    private static void BuildSkinnedMesh(BuildContext context, SkinnedRendererEntry entry)
    {
        ISkinnedMeshRenderer renderer = entry.Renderer;
        IMesh? mesh = renderer.MeshP;
        if (mesh is null || !context.TryGetOrMakeMeshData(mesh, out MeshData meshData))
        {
            return;
        }

        IMeshBuilder<MaterialBuilder> meshBuilder = BuildSubMeshes(context, mesh, meshData, renderer);
        string[] morphNames = AddMorphTargets(meshBuilder, mesh, meshData);

        // 关节:SkinnedMeshRenderer.Bones 的顺序就是顶点蒙皮索引(BoneWeight4.Index)引用的顺序。
        int boneCount = renderer.BonesP.Count;
        InstanceBuilder instance;
        if (boneCount > 0)
        {
            NodeBuilder[] jointNodes = new NodeBuilder[boneCount];
            int missing = 0;
            for (int i = 0; i < boneCount; i++)
            {
                ITransform? boneTransform = renderer.BonesP[i];
                if (boneTransform is not null && context.NodeByTransform.TryGetValue(boneTransform, out NodeBuilder? jointNode))
                {
                    jointNodes[i] = jointNode;
                }
                else
                {
                    // 兜底:缺失的骨骼挂到 renderer 节点,避免索引错位。
                    jointNodes[i] = entry.Node;
                    missing++;
                }
            }
            if (missing > 0)
            {
                Logger.Warning(LogCategory.Export, $"[GLB] '{renderer.GetBestName()}': {missing}/{boneCount} bones missing from hierarchy, skin will be partially rigid");
            }
            // 用关节节点的世界变换(= 绑定姿势层级)反推逆绑定矩阵,避开 Unity bindpose 的矩阵约定坑。
            instance = context.Scene.AddSkinnedMesh(meshBuilder, Matrix4x4.Identity, jointNodes);
        }
        else
        {
            instance = context.Scene.AddRigidMesh(meshBuilder, entry.Node);
        }

        if (morphNames.Length > 0)
        {
            context.MorphInstances[entry.Path] = new MorphInstance(instance, morphNames);
        }
    }

    private static void BuildStaticMesh(BuildContext context, IMeshFilter meshFilter, IRenderer renderer, NodeBuilder node)
    {
        if (!meshFilter.TryGetMesh(out IMesh? mesh) || !mesh.IsSet() || !context.TryGetOrMakeMeshData(mesh, out MeshData meshData))
        {
            return;
        }

        // 静态合批的子网格子集处理与 AR GlbLevelBuilder 一致(SubsetIndices/StaticBatchInfo)。
        if (HasStaticBatchSubset(renderer))
        {
            int[] subsetIndices = GetSubsetIndices(renderer);
            if (subsetIndices.Length == 0)
            {
                return;
            }
            (ISubMesh, MaterialBuilder)[] subMeshes = new (ISubMesh, MaterialBuilder)[subsetIndices.Length];
            for (int i = 0; i < subsetIndices.Length; i++)
            {
                subMeshes[i] = (mesh.SubMeshes[subsetIndices[i]], context.Materials.GetOrMakeMaterial(renderer, i));
            }
            IMeshBuilder<MaterialBuilder> batched = GlbSubMeshBuilder.BuildSubMeshes(
                new ArraySegment<(ISubMesh, MaterialBuilder)>(subMeshes), mesh.Is16BitIndices(), meshData,
                AssetRipper.Numerics.Transformation.Identity, AssetRipper.Numerics.Transformation.Identity);
            context.Scene.AddRigidMesh(batched, node);
            return;
        }

        IMeshBuilder<MaterialBuilder> meshBuilder = BuildSubMeshes(context, mesh, meshData, renderer);
        context.Scene.AddRigidMesh(meshBuilder, node);
    }

    private static IMeshBuilder<MaterialBuilder> BuildSubMeshes(BuildContext context, IMesh mesh, MeshData meshData, IRenderer renderer)
    {
        (ISubMesh, MaterialBuilder)[] subMeshes = new (ISubMesh, MaterialBuilder)[mesh.SubMeshes.Count];
        for (int i = 0; i < mesh.SubMeshes.Count; i++)
        {
            subMeshes[i] = (mesh.SubMeshes[i], context.Materials.GetOrMakeMaterial(renderer, i));
        }
        IMeshBuilder<MaterialBuilder> meshBuilder = GlbSubMeshBuilder.BuildSubMeshes(
            new ArraySegment<(ISubMesh, MaterialBuilder)>(subMeshes), mesh.Is16BitIndices(), meshData,
            AssetRipper.Numerics.Transformation.Identity, AssetRipper.Numerics.Transformation.Identity);
        meshBuilder.Name = mesh.GetBestName();
        return meshBuilder;
    }

    /// <summary>
    /// Unity blendshape channels -> glTF morph targets (each channel's last frame = weight-1 target),
    /// with Blender/three.js-conventional target names in mesh extras.
    /// </summary>
    private static string[] AddMorphTargets(IMeshBuilder<MaterialBuilder> meshBuilder, IMesh mesh, MeshData meshData)
    {
        IBlendShapeData? shapes = mesh.Shapes;
        if (shapes is null || shapes.Channels.Count == 0 || shapes.Vertices.Count == 0)
        {
            return Array.Empty<string>();
        }

        string[] names = new string[shapes.Channels.Count];
        for (int channelIndex = 0; channelIndex < shapes.Channels.Count; channelIndex++)
        {
            MeshBlendShapeChannel channel = shapes.Channels[channelIndex];
            names[channelIndex] = channel.Name_R.String;
            int lastFrame = channel.FrameIndex + channel.FrameCount - 1;
            if (lastFrame < 0 || lastFrame >= shapes.Shapes.Count)
            {
                continue;
            }
            MeshBlendShape_4_3 shape = shapes.Shapes[lastFrame];
            IMorphTargetBuilder morphTarget = meshBuilder.UseMorphTarget(channelIndex);
            uint end = shape.FirstVertex + shape.VertexCount;
            for (uint v = shape.FirstVertex; v < end && v < shapes.Vertices.Count; v++)
            {
                BlendShapeVertex blendVertex = shapes.Vertices[(int)v];
                if (blendVertex.Index >= meshData.Vertices.Length)
                {
                    continue;
                }
                Vector3 basePosition = GlbCoordinateConversion.ToGltfVector3Convert(meshData.Vertices[blendVertex.Index]);
                Vector3 positionDelta = GlbCoordinateConversion.ToGltfVector3Convert(blendVertex.Vertex.CastToStruct());
                Vector3 normalDelta = shape.HasNormals
                    ? GlbCoordinateConversion.ToGltfVector3Convert(blendVertex.Normal.CastToStruct())
                    : Vector3.Zero;
                Vector3 tangentDelta = shape.HasTangents
                    ? GlbCoordinateConversion.ToGltfVector3Convert(blendVertex.Tangent.CastToStruct())
                    : Vector3.Zero;
                morphTarget.SetVertexDelta(basePosition, new VertexGeometryDelta(positionDelta, normalDelta, tangentDelta));
            }
        }

        JsonArray targetNames = new();
        foreach (string name in names)
        {
            targetNames.Add(JsonValue.Create(name));
        }
        meshBuilder.Extras = new JsonObject { ["targetNames"] = targetNames };
        return names;
    }

    // ---- animation -------------------------------------------------------------------------

    private static void AddAnimations(BuildContext context)
    {
        HashSet<string> usedTrackNames = new(StringComparer.Ordinal);

        foreach (AnimatorEntry animatorEntry in context.Animators)
        {
            AvatarMuscleReferential? referential = null;
            IAvatar? avatar = animatorEntry.Animator.AvatarP;
            if (avatar is null)
            {
                Logger.Info(LogCategory.Export, $"[GLB] animator '{animatorEntry.Animator.GetBestName()}' has no Avatar — humanoid baking unavailable");
            }
            else
            {
                try
                {
                    referential = AvatarMuscleReferential.TryCreate(avatar);
                    Logger.Info(LogCategory.Export, referential is null
                        ? $"[GLB] avatar '{avatar.GetBestName()}' carries no human rig (generic avatar) — humanoid baking skipped"
                        : $"[GLB] avatar '{avatar.GetBestName()}': muscle referential with {referential.DrivenBones.Count} driven bones");
                }
                catch (Exception ex)
                {
                    Logger.Warning(LogCategory.Export, $"[GLB] avatar muscle referential failed for '{avatar.GetBestName()}': {ex.Message}");
                }
            }

            foreach (IAnimationClip clip in CollectClips(animatorEntry.Animator))
            {
                if (!context.SeenClips.Add(clip))
                {
                    continue;
                }
                string trackName = UniqueTrackName(usedTrackNames, clip.GetBestName());
                try
                {
                    AddClip(context, animatorEntry, clip, referential, trackName);
                }
                catch (Exception ex)
                {
                    Logger.Warning(LogCategory.Export, $"[GLB] animation failed for '{trackName}': {ex.Message}");
                }
            }
        }
    }

    private static void AddClip(BuildContext context, AnimatorEntry animatorEntry, IAnimationClip clip,
        AvatarMuscleReferential? referential, string trackName)
    {
        string basePath = animatorEntry.Path;
        AddGenericCurves(context, clip, basePath, trackName);
        AddMorphWeightCurves(context, clip, basePath, trackName);

        if (referential is not null)
        {
            int humanoidTracks = HumanoidClipBaker.Bake(clip, referential,
                new PrefixedPathLookup<NodeBuilder>(context.NodeByPath, basePath),
                new PrefixedPathLookup<UnityLocalTransform>(context.RestByPath, basePath),
                trackName);
            if (humanoidTracks > 0)
            {
                Logger.Info(LogCategory.Export, $"[GLB] humanoid clip '{trackName}': baked {humanoidTracks} bone tracks");
            }
        }
    }

    private static void AddGenericCurves(BuildContext context, IAnimationClip clip, string basePath, string trackName)
    {
        for (int i = 0; i < clip.PositionCurves_C74.Count; i++)
        {
            IVector3Curve curve = clip.PositionCurves_C74[i];
            if (!TryGetNode(context, basePath, curve.Path.String, out NodeBuilder? node))
            {
                continue;
            }
            SharpGLTF.Animations.CurveBuilder<Vector3> track = node.UseTranslation(trackName);
            for (int k = 0; k < curve.Curve.Curve.Count; k++)
            {
                IKeyframe_Vector3f key = curve.Curve.Curve[k];
                track.SetPoint(key.Time, GlbCoordinateConversion.ToGltfVector3Convert(key.Value.CastToStruct()));
            }
        }

        for (int i = 0; i < clip.RotationCurves_C74.Count; i++)
        {
            IQuaternionCurve curve = clip.RotationCurves_C74[i];
            if (!TryGetNode(context, basePath, curve.Path.String, out NodeBuilder? node))
            {
                continue;
            }
            SharpGLTF.Animations.CurveBuilder<Quaternion> track = node.UseRotation(trackName);
            for (int k = 0; k < curve.Curve.Curve.Count; k++)
            {
                IKeyframe_Quaternionf key = curve.Curve.Curve[k];
                track.SetPoint(key.Time, GlbCoordinateConversion.ToGltfQuaternionConvert(key.Value.CastToStruct()));
            }
        }

        for (int i = 0; i < clip.ScaleCurves_C74.Count; i++)
        {
            IVector3Curve curve = clip.ScaleCurves_C74[i];
            if (!TryGetNode(context, basePath, curve.Path.String, out NodeBuilder? node))
            {
                continue;
            }
            SharpGLTF.Animations.CurveBuilder<Vector3> track = node.UseScale(trackName);
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
            if (!TryGetNode(context, basePath, curve.Path.String, out NodeBuilder? node))
            {
                continue;
            }
            SharpGLTF.Animations.CurveBuilder<Quaternion> track = node.UseRotation(trackName);
            for (int k = 0; k < curve.Curve.Curve.Count; k++)
            {
                IKeyframe_Vector3f key = curve.Curve.Curve[k];
                Quaternion unityQuat = EulerToQuaternion(key.Value.CastToStruct());
                track.SetPoint(key.Time, GlbCoordinateConversion.ToGltfQuaternionConvert(unityQuat));
            }
        }
    }

    /// <summary>blendShape.&lt;name&gt; float curves -> glTF morph weight tracks (values /100, sampled at clip rate).</summary>
    private static void AddMorphWeightCurves(BuildContext context, IAnimationClip clip, string basePath, string trackName)
    {
        Dictionary<string, List<(int ChannelIndex, HermiteCurve Curve)>>? curvesByMeshPath = null;
        foreach (IFloatCurve floatCurve in clip.FloatCurves_C74)
        {
            const string BlendShapePrefix = "blendShape.";
            string attribute = floatCurve.Attribute.String;
            if (!attribute.StartsWith(BlendShapePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            string meshPath = ResolvePath(basePath, floatCurve.Path.String);
            if (!context.MorphInstances.TryGetValue(meshPath, out MorphInstance morphInstance))
            {
                continue;
            }
            int channelIndex = Array.IndexOf(morphInstance.ChannelNames, attribute[BlendShapePrefix.Length..]);
            if (channelIndex < 0)
            {
                continue;
            }
            HermiteCurve curve = HermiteCurve.FromKeyframes(floatCurve.Curve.Curve);
            if (curve.KeyCount == 0)
            {
                continue;
            }
            curvesByMeshPath ??= new Dictionary<string, List<(int, HermiteCurve)>>(StringComparer.Ordinal);
            if (!curvesByMeshPath.TryGetValue(meshPath, out List<(int, HermiteCurve)>? list))
            {
                curvesByMeshPath[meshPath] = list = new List<(int, HermiteCurve)>();
            }
            list.Add((channelIndex, curve));
        }

        if (curvesByMeshPath is null)
        {
            return;
        }

        float sampleRate = clip.SampleRate_C74 > 0f ? clip.SampleRate_C74 : 60f;
        foreach ((string meshPath, List<(int ChannelIndex, HermiteCurve Curve)> curves) in curvesByMeshPath)
        {
            MorphInstance morphInstance = context.MorphInstances[meshPath];
            float duration = 0f;
            foreach ((_, HermiteCurve curve) in curves)
            {
                duration = MathF.Max(duration, curve.LastTime);
            }
            int frameCount = Math.Max(1, (int)MathF.Round(duration * sampleRate) + 1);

            SharpGLTF.Animations.CurveBuilder<ArraySegment<float>> track =
                morphInstance.Instance.Content.UseMorphing(trackName);
            for (int f = 0; f < frameCount; f++)
            {
                float time = f / sampleRate;
                float[] weights = new float[morphInstance.ChannelNames.Length];
                foreach ((int channelIndex, HermiteCurve curve) in curves)
                {
                    weights[channelIndex] = curve.Evaluate(time) / 100f;
                }
                track.SetPoint(time, new ArraySegment<float>(weights), true);
            }
        }
    }

    private static List<IAnimationClip> CollectClips(IAnimator animator)
    {
        List<IAnimationClip> clips = new();
        HashSet<IAnimationClip> seen = new();
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

    // ---- helpers ---------------------------------------------------------------------------

    private static bool TryGetNode(BuildContext context, string basePath, string curvePath, [NotNullWhen(true)] out NodeBuilder? node)
    {
        return context.NodeByPath.TryGetValue(ResolvePath(basePath, curvePath), out node);
    }

    /// <summary>曲线路径相对 Animator 所在节点;animator 挂在 prefab 根时前缀为 ""。</summary>
    internal static string ResolvePath(string basePath, string curvePath)
    {
        if (basePath.Length == 0)
        {
            return curvePath;
        }
        return curvePath.Length == 0 ? basePath : basePath + "/" + curvePath;
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

    private static bool HasStaticBatchSubset(IRenderer renderer)
    {
        return (renderer.Has_StaticBatchInfo_C25() && renderer.StaticBatchInfo_C25.SubMeshCount > 0)
            || (renderer.Has_SubsetIndices_C25() && renderer.SubsetIndices_C25.Count > 0);
    }

    private static int[] GetSubsetIndices(IRenderer renderer)
    {
        if (renderer.Has_SubsetIndices_C25())
        {
            return renderer.SubsetIndices_C25.Select(i => (int)i).ToArray();
        }
        if (renderer.Has_StaticBatchInfo_C25())
        {
            return Enumerable.Range(renderer.StaticBatchInfo_C25.FirstSubMesh, renderer.StaticBatchInfo_C25.SubMeshCount).ToArray();
        }
        return Array.Empty<int>();
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

    private static Vector3 ToVector3(IXform xform)
    {
        if (xform.Has_T3())
        {
            return new Vector3(xform.T3.X, xform.T3.Y, xform.T3.Z);
        }
        Vector4Float_4? t4 = xform.T4;
        return t4 is null ? Vector3.Zero : new Vector3(t4.X, t4.Y, t4.Z);
    }

    private static Vector3 ToScale(IXform xform)
    {
        if (xform.Has_S3())
        {
            return new Vector3(xform.S3.X, xform.S3.Y, xform.S3.Z);
        }
        Vector4Float_4? s4 = xform.S4;
        return s4 is null ? Vector3.One : new Vector3(s4.X, s4.Y, s4.Z);
    }

    private static string UniqueTrackName(HashSet<string> used, string name)
    {
        if (used.Add(name))
        {
            return name;
        }
        for (int i = 2; ; i++)
        {
            string candidate = $"{name}_{i}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    // ---- context ---------------------------------------------------------------------------

    private sealed class BuildContext
    {
        public SceneBuilder Scene { get; }
        public bool IsScene { get; }
        public RuriGlbMaterialCache Materials { get; } = new();
        public Dictionary<ITransform, NodeBuilder> NodeByTransform { get; } = new();
        public Dictionary<string, NodeBuilder> NodeByPath { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, UnityLocalTransform> RestByPath { get; } = new(StringComparer.Ordinal);
        public List<SkinnedRendererEntry> SkinnedRenderers { get; } = new();
        public List<(IMeshFilter Filter, IRenderer Renderer, NodeBuilder Node)> StaticRenderers { get; } = new();
        public List<AnimatorEntry> Animators { get; } = new();
        public HashSet<IUnityObjectBase> Exported { get; } = new();
        public HashSet<IAnimationClip> SeenClips { get; } = new();
        public Dictionary<string, MorphInstance> MorphInstances { get; } = new(StringComparer.Ordinal);

        private readonly Dictionary<IMesh, MeshData> _meshCache = new();
        private readonly HashSet<string> _usedNodeNames = new(StringComparer.Ordinal);

        public string UniqueNodeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                name = "node";
            }
            if (_usedNodeNames.Add(name))
            {
                return name;
            }
            for (int i = 1; ; i++)
            {
                string candidate = $"{name}_{i}";
                if (_usedNodeNames.Add(candidate))
                {
                    return candidate;
                }
            }
        }

        public BuildContext(SceneBuilder scene, bool isScene)
        {
            Scene = scene;
            IsScene = isScene;
        }

        public bool TryGetOrMakeMeshData(IMesh mesh, out MeshData meshData)
        {
            if (_meshCache.TryGetValue(mesh, out meshData))
            {
                return true;
            }
            if (MeshData.TryMakeFromMesh(mesh, out meshData))
            {
                _meshCache.Add(mesh, meshData);
                return true;
            }
            return false;
        }
    }

    private readonly record struct SkinnedRendererEntry(ISkinnedMeshRenderer Renderer, NodeBuilder Node, string Path);

    private readonly record struct AnimatorEntry(IAnimator Animator, NodeBuilder Node, string Path);

    private readonly record struct MorphInstance(InstanceBuilder Instance, string[] ChannelNames);

    /// <summary>
    /// 把「animator 相对路径」查询映射到「prefab 根相对路径」字典上的零拷贝视图。
    /// 契约:只有 TryGetValue/ContainsKey/索引器做前缀映射;Keys/Values/枚举透传底层字典
    /// (键是 prefab 根相对路径)——烘焙器只按键查,不要拿这个视图做枚举。
    /// </summary>
    private sealed class PrefixedPathLookup<TValue> : IReadOnlyDictionary<string, TValue>
    {
        private readonly IReadOnlyDictionary<string, TValue> _inner;
        private readonly string _basePath;

        public PrefixedPathLookup(IReadOnlyDictionary<string, TValue> inner, string basePath)
        {
            _inner = inner;
            _basePath = basePath;
        }

        public TValue this[string key] => _inner[ResolvePath(_basePath, key)];
        public IEnumerable<string> Keys => _inner.Keys;
        public IEnumerable<TValue> Values => _inner.Values;
        public int Count => _inner.Count;
        public bool ContainsKey(string key) => _inner.ContainsKey(ResolvePath(_basePath, key));
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out TValue value) => _inner.TryGetValue(ResolvePath(_basePath, key), out value);
        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
