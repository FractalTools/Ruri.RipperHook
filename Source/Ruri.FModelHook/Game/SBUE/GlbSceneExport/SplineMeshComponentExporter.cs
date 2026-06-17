using System;
using System.Collections.Generic;
using System.Reflection;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Objects.RenderCore;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Faithful 1:1 port of USplineMeshComponent CPU deformation, materialised as a
// per-placement deformed UStaticMesh that flows through the SAME
// GlbSceneContext.AddRigidMesh -> BuildMesh -> Gltf.ExportStaticMeshSections
// pipeline the StaticMeshComponentExporter uses. Geometry bytes therefore stay
// byte-identical to "FModel Save Model" output for the deformed slice
// positions/normals/tangents, just like the straight-static path does for
// undeformed meshes.
//
// Ground-truth sources (READ AND MIRRORED LINE BY LINE):
//   * CUE4Parse FSplineMeshParams.cs:65-83 — SplineEvalPos/SplineEvalTangent/
//     SplineEvalDir (cubic Hermite). DO NOT re-derive the Hermite coefficients;
//     call SplineParams.SplineEvalPos / SplineEvalTangent / SplineEvalDir.
//   * CUE4Parse USplineMeshComponent.cs:104-181 — CalcSliceTransform /
//     CalcSliceTransformAtSplineOffset (SmoothStep, BaseXVec=SplineUpDir^SplineDir,
//     BaseYVec=SplineDir^BaseXVec, sliceOffset/roll/scale lerp, 3-axis FTransform
//     basis switch). DO NOT re-implement; call spline.CalcSliceTransform.
//   * CUE4Parse MeshConverter.cs:154-160 — the verified CPU per-vertex
//     deformation: read distanceAlong=GetAxisValueRef(pos, ForwardAxis); compute
//     sliceTransform; SetAxisValueRef(pos, ForwardAxis, 0); pos =
//     sliceTransform.TransformPosition(pos). This is the byte-for-byte CPU
//     identical algorithm to UE 5.7.4
//     Engine/Private/Components/SplineMeshComponent.cpp:1013 (collision export
//     path: `Vertex = CalcSliceTransform(GetAxisValueRef(Vertex, ForwardAxis))
//     .TransformPosition(Vertex * Mask)` where Mask zeros the forward axis).
//   * UE 5.7.4 Engine/Shaders/Private/SplineMeshCommon.ush:240-258
//     (SplineMeshDeformLocalPosNormalTangent) — the GPU truth confirms that
//     normal AND tangent rotate by the slice's rotation matrix (no scale). The
//     CUE4Parse CPU port has a "TODO normals" at MeshConverter.cs:154 and skips
//     this rotation; we deliberately include it because the task mandates
//     "位置/法线/切线都按 slice 变换旋转". The rotation source is
//     sliceTransform.TransformVectorNoScale (FTransform.cs:400, pure rotation,
//     matches the shader's `mul(LocalNormal, SliceRot)`).
//
// Why a cloned UStaticMesh instead of a separate SharpGLTF pipeline:
//   GlbSceneContext.AddRigidMesh's public surface only accepts a UStaticMesh
//   and is intentionally frozen per the cell contract. The faithful way to
//   land deformed geometry inside the SAME .glb part files (so the spline
//   actor's render contribution is not banished to a sidecar file) is to feed
//   the existing BuildMesh path a UStaticMesh whose RenderData LOD0
//   PositionVertexBuffer / VertexBuffer.UV already carry the bent attributes.
//   The bend is deterministic from (mesh.LightingGuid, SplineParams.GetSHAHash),
//   so identical bends across multiple placements share one MeshBuilder via
//   the context's mesh cache; different bends emit distinct MeshBuilders.
//
// Why the clone path does not corrupt CUE4Parse's asset cache:
//   The CUE4Parse asset cache stores the source UStaticMesh keyed by its
//   package path. We never mutate the source instance. Every clone is built
//   bottom-up from `new UStaticMesh()` + a fresh `FStaticMeshRenderData` +
//   freshly-allocated `FPositionVertexBuffer.Verts` / `FStaticMeshUVItem[]`,
//   borrowing only the read-only `IndexBuffer` / `ColorVertexBuffer` /
//   `Sections` references the existing TryConvert path consumes without
//   writing back. Materials and StaticMaterials arrays are also shared by
//   reference — read-only after deserialize. The cloned mesh's LightingGuid is
//   stamped uniquely per bend so the mesh-share cache in GlbSceneContext keeps
//   the bent clone separate from the base mesh and from other bends.
public sealed class SplineMeshComponentExporter : IComponentExporter
{
    // Mesh-cache key: identical (source mesh + spline bend) tuples MUST share
    // one cloned UStaticMesh so the downstream mesh-share cache instances
    // them. The bend signature is exactly FSplineMeshParams.GetSHAHash, which
    // hashes every spline-affecting parameter byte-for-byte
    // (FSplineMeshParams.cs:86-113) PLUS the spline-frame parameters that live
    // on the component itself rather than the params struct (ForwardAxis,
    // SplineUpDir, SplineBoundaryMin/Max, bSmoothInterpRollScale). Two splines
    // whose params hash matches but whose ForwardAxis differs deform
    // differently (the basis switch in CalcSliceTransformAtSplineOffset
    // changes the rotation column → mesh bytes differ), so the cache key MUST
    // include those bits as well.
    private readonly Dictionary<DeformedMeshKey, UStaticMesh> _deformedMeshCache = new();

    public bool CanExport(UObject component) => component is USplineMeshComponent;

    public void Export(in PlacedComponent placed, GlbSceneContext context)
    {
        if (placed.Component is not USplineMeshComponent spline) return;

        UStaticMesh? sourceMesh = spline.GetStaticMesh().Load<UStaticMesh>();
        if (sourceMesh == null || sourceMesh.Materials.Length < 1)
        {
            // Mirror StaticMeshComponentExporter.cs:67-71 — a SplineMesh with no
            // bound static mesh (or zero materials) silently contributes nothing
            // to the render layer; the lossless layer still captures the
            // component's full property tree, so no data is lost overall.
            return;
        }

        DeformedMeshKey key = BuildDeformedMeshKey(sourceMesh, spline);
        if (!_deformedMeshCache.TryGetValue(key, out UStaticMesh? deformedMesh))
        {
            deformedMesh = BuildDeformedClone(sourceMesh, spline, key.UniqueLightingGuid, context);
            _deformedMeshCache[key] = deformedMesh;
        }
        if (deformedMesh == null)
        {
            // The deformation builder logged the reason (no RenderData / no
            // LODs / no PositionVertexBuffer). Skip without recording the
            // placement to manifest.Dropped for the same reason the static
            // exporter skips a meshless ISM entry: the lossless actor JSON
            // still preserves the spline component data.
            return;
        }

        // OverrideMaterials handling mirrors the verified Renderer path
        // (Renderer.cs:642-652) byte-for-byte through
        // StaticMeshComponentExporter.BuildOverrideMaterialLists. Doing it
        // identically here keeps the mesh-share key contributions consistent
        // across the two exporters: an overridden bent mesh + a bent mesh with
        // no overrides yield distinct MeshBuilders inside GlbSceneContext,
        // exactly like the straight-static path does for ISM placements.
        BuildOverrideMaterialLists(
            spline,
            out IReadOnlyList<UMaterialInterface?> overrideMaterials,
            out IReadOnlyList<string> overrideMaterialPathNames);

        // The deformed positions are expressed in the spline component's
        // LOCAL space (same convention as MeshConverter.cs:154-160 — the
        // CalcSliceTransform output lives in component space because both
        // splinePos and the basis vectors are derived from SplineParams.*,
        // which FSplineMeshParams.cs:15/31 documents as "in component space").
        // So the placement's world transform (which already folds the
        // AttachParent chain via SceneTransform.CalculateTransform) is handed
        // straight through SceneTransform.NodeMatrix (N = W, FModel's verified
        // placement matrix) to put the bent mesh exactly where the verified
        // preview puts the straight mesh of a non-spline component.
        context.AddRigidMesh(
            deformedMesh,
            overrideMaterials,
            overrideMaterialPathNames,
            SceneTransform.NodeMatrix(placed.WorldTransform));
    }

    // 1:1 mirror of StaticMeshComponentExporter.BuildOverrideMaterialLists
    // (kept duplicated rather than refactored upstream because the static-mesh
    // exporter file is owned by another cell). The duplication MUST stay
    // byte-faithful — any divergence in the cache-signature derivation would
    // re-introduce the write-side/read-side key inconsistency the foundation
    // contract calls out explicitly.
    private static void BuildOverrideMaterialLists(
        UObject component,
        out IReadOnlyList<UMaterialInterface?> overrideMaterials,
        out IReadOnlyList<string> overrideMaterialPathNames)
    {
        if (!component.TryGetValue(out FPackageIndex[] overrideMaterialIndices, "OverrideMaterials") ||
            overrideMaterialIndices.Length == 0)
        {
            overrideMaterials = Array.Empty<UMaterialInterface?>();
            overrideMaterialPathNames = Array.Empty<string>();
            return;
        }

        var loadedMaterials = new UMaterialInterface?[overrideMaterialIndices.Length];
        var pathNames = new string[overrideMaterialIndices.Length];
        for (int materialSlot = 0; materialSlot < overrideMaterialIndices.Length; materialSlot++)
        {
            var overrideIndex = overrideMaterialIndices[materialSlot];
            if (overrideIndex == null || overrideIndex.IsNull)
            {
                pathNames[materialSlot] = string.Empty;
                continue;
            }

            if (overrideIndex.Load() is UMaterialInterface material)
            {
                loadedMaterials[materialSlot] = material;
                pathNames[materialSlot] = material.GetPathName();
            }
            else
            {
                pathNames[materialSlot] = overrideIndex.Name ?? string.Empty;
            }
        }
        overrideMaterials = loadedMaterials;
        overrideMaterialPathNames = pathNames;
    }

    // Cache signature for "this exact source mesh bent with this exact spline".
    // The unique LightingGuid stamped onto the cloned UStaticMesh is also
    // derived from this signature, so it feeds the downstream GlbSceneContext
    // mesh-share cache (which keys on LightingGuid + override-material list)
    // and lets identical bends with identical overrides share one MeshBuilder.
    private static DeformedMeshKey BuildDeformedMeshKey(UStaticMesh sourceMesh, USplineMeshComponent spline)
    {
        // SplineParams.GetSHAHash hashes the 13 spline-params floats
        // (FSplineMeshParams.cs:86-113). Hash the FOUR additional component
        // bits that affect the slice math: ForwardAxis (enum int),
        // SplineBoundaryMin/Max (custom-boundary T-range), SplineUpDir (basis
        // X seed), bSmoothInterpRollScale (lerp curve). Without these the same
        // params on different axes would collide. Pack the 32 derived bytes
        // into the FGuid's four uint slots and XOR with the source mesh's
        // LightingGuid so the bent clone never collides with the base mesh in
        // GlbSceneContext.
        string splineParamsHashHex = spline.SplineParams.GetSHAHash();
        // The hash is 64 hex chars (SHA-256). Take the first 16 bytes (32 hex
        // chars) as 4 uints — enough entropy to be effectively collision-free
        // at scene scale, identical for identical bends.
        Span<uint> hashWords = stackalloc uint[4];
        for (int hashWordIndex = 0; hashWordIndex < 4; hashWordIndex++)
        {
            hashWords[hashWordIndex] = uint.Parse(
                splineParamsHashHex.AsSpan(hashWordIndex * 8, 8),
                System.Globalization.NumberStyles.HexNumber);
        }

        // Fold the component-side variations in. ForwardAxis is the highest-
        // impact bit (changes the rotation column), so mask it into slot 0.
        uint forwardAxisBits = (uint)(int)spline.ForwardAxis;
        uint upDirBits = HashFloatTriple(spline.SplineUpDir.X, spline.SplineUpDir.Y, spline.SplineUpDir.Z);
        uint boundaryBits = HashFloatTriple(spline.SplineBoundaryMin, spline.SplineBoundaryMax, spline.bSmoothInterpRollScale ? 1f : 0f);
        hashWords[0] ^= forwardAxisBits;
        hashWords[1] ^= upDirBits;
        hashWords[2] ^= boundaryBits;

        FGuid uniqueLightingGuid = new(
            sourceMesh.LightingGuid.A ^ hashWords[0],
            sourceMesh.LightingGuid.B ^ hashWords[1],
            sourceMesh.LightingGuid.C ^ hashWords[2],
            sourceMesh.LightingGuid.D ^ hashWords[3]);

        return new DeformedMeshKey(sourceMesh.LightingGuid, splineParamsHashHex, forwardAxisBits, uniqueLightingGuid);
    }

    // Cheap, deterministic mix of three floats into one uint. The exact mixer
    // is irrelevant — only required property is "different inputs ~always map
    // to different outputs" so the cache key disambiguates. SHA on the bits
    // would be overkill at scene scale; HashCode.Combine is good enough.
    private static uint HashFloatTriple(float a, float b, float c)
    {
        // HashCode.Combine returns a signed int that is negative roughly half
        // the time; this assembly builds with <CheckForOverflowUnderflow>true</>
        // (Source/Directory.Build.props), so a plain (uint) cast of a negative
        // value throws OverflowException at runtime — which silently dropped
        // every spline actor (the bend key is computed before the deform, so the
        // mesh never reached the scene). `unchecked` restores the intended
        // bit-reinterpretation: the mixer only needs "different inputs ~always
        // map to different bits", and the wrap is exactly that.
        return unchecked((uint)HashCode.Combine(a, b, c));
    }

    // Build a fresh UStaticMesh whose LOD0 PositionVertexBuffer / VertexBuffer
    // hold the spline-deformed positions / normals / tangents. The clone
    // shares (by reference) every read-only structure the downstream
    // TryConvert path consumes — IndexBuffer, ColorVertexBuffer, Sections,
    // Materials, StaticMaterials — so the bent mesh emits the same triangle
    // indices, the same per-section material slots, the same per-vertex
    // colours, and the same UV channels as the unbent source. Only positions
    // / normals / tangents are rewritten.
    //
    // Returns null only when the source has no usable RenderData LOD0
    // (e.g. Nanite-only meshes whose normal LODs were stripped at cook). The
    // caller silently skips that placement, mirroring the static-mesh
    // exporter's `mesh.Materials.Length < 1` skip.
    private static UStaticMesh? BuildDeformedClone(
        UStaticMesh sourceMesh,
        USplineMeshComponent spline,
        FGuid uniqueLightingGuid,
        GlbSceneContext context)
    {
        FStaticMeshRenderData? sourceRenderData = sourceMesh.RenderData;
        if (sourceRenderData == null || sourceRenderData.LODs == null || sourceRenderData.LODs.Length == 0)
        {
            context.LogError($"[GlbScene] SplineMesh source '{sourceMesh.Name}' has no RenderData; spline placement skipped.");
            return null;
        }

        // Build the deformed LOD array. Only LODs with a usable
        // PositionVertexBuffer are reachable through the static path
        // (FStaticMeshLODResources.SkipLod gates it at TryConvert), so we
        // deform exactly those and copy the rest by reference.
        var clonedLods = new FStaticMeshLODResources[sourceRenderData.LODs.Length];
        bool deformedAnyLod = false;
        for (int lodIndex = 0; lodIndex < sourceRenderData.LODs.Length; lodIndex++)
        {
            FStaticMeshLODResources sourceLod = sourceRenderData.LODs[lodIndex];
            if (sourceLod.SkipLod)
            {
                clonedLods[lodIndex] = sourceLod;
                continue;
            }
            clonedLods[lodIndex] = DeformLod(sourceLod, spline);
            deformedAnyLod = true;
        }
        if (!deformedAnyLod)
        {
            context.LogError($"[GlbScene] SplineMesh source '{sourceMesh.Name}' has no usable LOD vertex buffers; spline placement skipped.");
            return null;
        }

        // Clone the RenderData container (the LODs array we just built plus a
        // shallow copy of the bounds / screen sizes / nanite resources). The
        // bounds we hand back are the SOURCE bounds, NOT post-deformation
        // bounds — TryConvert reads `Bounds.Origin/BoxExtent` for the
        // CStaticMesh BoundingBox / BoundingSphere it sets on convertedMesh
        // (MeshConverter.cs:80-83), and Gltf.ExportStaticMeshSections does not
        // consume those bounds, so the value only affects downstream culling
        // metadata and never the emitted glTF vertex bytes. The source bounds
        // are the safest "I have not measured the bent envelope" answer.
        var clonedRenderData = new FStaticMeshRenderData
        {
            LODs = clonedLods,
            // NaniteResources is intentionally NOT carried over: TryConvert's
            // nanite branch (MeshConverter.cs:182-201) would otherwise replace
            // the deformed normal LODs with the source nanite cluster geometry,
            // erasing the deformation. Splines on nanite-only meshes therefore
            // fall through to the regular-LOD path; if the source has no
            // normal LODs at all the deformedAnyLod gate above already
            // returned null.
            NaniteResources = null,
            Bounds = sourceRenderData.Bounds,
            bLODsShareStaticLighting = sourceRenderData.bLODsShareStaticLighting,
            ScreenSize = sourceRenderData.ScreenSize,
        };

        // Build the fresh UStaticMesh. Property setters that are `private set`
        // on the CUE4Parse base class (RenderData, LightingGuid, BodySetup,
        // NavCollision, Sockets, StaticMaterials, LODForCollision) are written
        // through the backing field via reflection so the clone is fully
        // populated without touching the source.
        var clonedMesh = new UStaticMesh
        {
            Name = sourceMesh.Name,
            Class = sourceMesh.Class,
            Outer = sourceMesh.Outer,
            Super = sourceMesh.Super,
            Template = sourceMesh.Template,
            Flags = sourceMesh.Flags,
        };
        // The remaining state lives on properties with `private set`
        // accessors (UStaticMesh.cs:14-22). Reflection writes through the
        // compiler-generated backing field so we do not subclass / extend the
        // CUE4Parse type, only stamp the values we want onto a fresh shell.
        SetPrivateProperty(clonedMesh, nameof(UStaticMesh.Materials), sourceMesh.Materials);
        SetPrivateProperty(clonedMesh, nameof(UStaticMesh.RenderData), clonedRenderData);
        SetPrivateProperty(clonedMesh, nameof(UStaticMesh.LightingGuid), uniqueLightingGuid);
        SetPrivateProperty(clonedMesh, nameof(UStaticMesh.BodySetup), sourceMesh.BodySetup);
        SetPrivateProperty(clonedMesh, nameof(UStaticMesh.NavCollision), sourceMesh.NavCollision);
        SetPrivateProperty(clonedMesh, nameof(UStaticMesh.Sockets), sourceMesh.Sockets);
        SetPrivateProperty(clonedMesh, nameof(UStaticMesh.StaticMaterials), sourceMesh.StaticMaterials);
        SetPrivateProperty(clonedMesh, nameof(UStaticMesh.LODForCollision), sourceMesh.LODForCollision);
        return clonedMesh;
    }

    // Reflection helper for CUE4Parse properties declared with `{ get; private set; }`.
    // Setting via PropertyInfo.SetValue would honour the `private` accessor at
    // method-resolution time and throw on read-only target types; routing the
    // write through the auto-property's backing field is the canonical way to
    // bypass the access modifier on a CUE4Parse exposed surface without
    // touching the cached source instance.
    private static void SetPrivateProperty(object target, string propertyName, object? value)
    {
        FieldInfo? backingField = target.GetType().GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (backingField != null)
        {
            backingField.SetValue(target, value);
            return;
        }
        // Fall back to PropertyInfo with non-public setter access — works when
        // the property uses a custom backing field rather than the compiler-
        // generated `<X>k__BackingField` pattern.
        PropertyInfo? property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        property?.GetSetMethod(true)?.Invoke(target, new[] { value });
    }

    // Per-LOD deformation. The cloned LOD shares (by reference) the IndexBuffer,
    // ReversedIndexBuffer, DepthOnlyIndexBuffer, ReversedDepthOnlyIndexBuffer,
    // WireframeIndexBuffer, AdjacencyIndexBuffer, ColorVertexBuffer,
    // CardRepresentationData, and Sections — TryConvert only reads them
    // (MeshConverter.cs:113-141, line 146 ColorVertexBuffer read, line 174
    // VertexColors write to a fresh CStaticMeshLod-side buffer) so sharing is
    // safe. PositionVertexBuffer (positions) and VertexBuffer (normals/
    // tangents in UV[i].Normal[]) are rebuilt with the bent values.
    private static FStaticMeshLODResources DeformLod(FStaticMeshLODResources sourceLod, USplineMeshComponent spline)
    {
        // MemberwiseClone copies every field — public, internal, private —
        // including the read-only auto-property backing fields for
        // Sections/MaxDeviation/IndexBuffer/etc. The shared references are
        // exactly what we want; we only override the two attribute buffers.
        var clonedLod = (FStaticMeshLODResources)CallMemberwiseClone(sourceLod);

        // Position vertex buffer: fresh instance, deformed Verts array.
        FPositionVertexBuffer sourcePvb = sourceLod.PositionVertexBuffer!;
        var deformedPvb = new FPositionVertexBuffer
        {
            Stride = sourcePvb.Stride,
            NumVertices = sourcePvb.NumVertices,
            Verts = new FVector[sourcePvb.Verts.Length],
        };

        // Vertex (normal/tangent) buffer: fresh instance, fresh UV array with
        // rotated normals; UV coordinates per channel are NOT touched (they're
        // shared by reference through FMeshUVFloat[] inside each item).
        FStaticMeshVertexBuffer sourceVb = sourceLod.VertexBuffer!;
        var deformedVb = (FStaticMeshVertexBuffer)CallMemberwiseClone(sourceVb);
        deformedVb.UV = new FStaticMeshUVItem[sourceVb.UV.Length];

        // Per-vertex deformation. MUST mirror MeshConverter.cs:154-160 for
        // positions (the verified CPU port that CUE4Parse uses; also matches
        // UE 5.7.4 Engine/Private/Components/SplineMeshComponent.cpp:1013), and
        // additionally rotates Normal (TangentZ) and Tangent (TangentX) by the
        // slice's rotation matrix exactly like the GPU vertex shader does
        // (Engine/Shaders/Private/SplineMeshCommon.ush:240-258). The shader's
        // SliceRot omits the slice scale, so TransformVectorNoScale is the
        // correct vehicle (FTransform.cs:400, pure quaternion rotation).
        ESplineMeshAxis forwardAxis = spline.ForwardAxis;
        FVector[] sourceVerts = sourcePvb.Verts;
        FVector[] deformedVerts = deformedPvb.Verts;
        FStaticMeshUVItem[] sourceUv = sourceVb.UV;
        FStaticMeshUVItem[] deformedUv = deformedVb.UV;
        int vertexCount = sourceVerts.Length;
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            // ---- POSITION (1:1 MeshConverter.cs:154-160) ------------------
            FVector localPosition = sourceVerts[vertexIndex];
            float distanceAlong = USplineMeshComponent.GetAxisValueRef(ref localPosition, forwardAxis);
            FTransform sliceTransform = spline.CalcSliceTransform(distanceAlong);
            USplineMeshComponent.SetAxisValueRef(ref localPosition, forwardAxis, 0f);
            deformedVerts[vertexIndex] = sliceTransform.TransformPosition(localPosition);

            // ---- NORMAL + TANGENT (1:1 SplineMeshCommon.ush:240-258) -------
            // FStaticMeshUVItem.Normal is [TangentX, TangentY (deprecated/0),
            // TangentZ] per FStaticMeshUVItem.cs:30. We re-pack TangentX and
            // TangentZ; TangentY is a derived bitangent and the engine
            // rebuilds it at runtime from the cross product (UE convention),
            // so we leave the middle slot at the source value (zero on
            // post-UE3 cooks) — keeping the existing CUE4Parse round-trip
            // semantics intact. The W bit of TangentX carries the basis sign
            // and is preserved by copying the source packed byte through
            // before re-packing the XYZ from the rotated vector.
            FPackedNormal[] sourceItemNormals = sourceUv[vertexIndex].Normal;
            FPackedNormal sourceTangentX = sourceItemNormals[0];
            FPackedNormal sourceTangentY = sourceItemNormals[1];
            FPackedNormal sourceTangentZ = sourceItemNormals[2];

            FVector localTangentX = (FVector)sourceTangentX;
            FVector localTangentZ = (FVector)sourceTangentZ;

            FVector rotatedTangentX = sliceTransform.TransformVectorNoScale(localTangentX).GetSafeNormal();
            FVector rotatedTangentZ = sliceTransform.TransformVectorNoScale(localTangentZ).GetSafeNormal();

            FPackedNormal rotatedPackedTangentX = PackTangentWithSignByte(rotatedTangentX, sourceTangentX);
            FPackedNormal rotatedPackedTangentZ = PackTangentWithSignByte(rotatedTangentZ, sourceTangentZ);

            deformedUv[vertexIndex] = new FStaticMeshUVItem(
                new[] { rotatedPackedTangentX, sourceTangentY, rotatedPackedTangentZ },
                sourceUv[vertexIndex].UV);
        }

        // Plug the deformed buffers into the cloned LOD. PositionVertexBuffer
        // has a public setter so it is a direct assignment; VertexBuffer has a
        // private setter and goes through the backing field.
        clonedLod.PositionVertexBuffer = deformedPvb;
        SetPrivateProperty(clonedLod, nameof(FStaticMeshLODResources.VertexBuffer), deformedVb);
        return clonedLod;
    }

    // Pack a rotated unit vector into FPackedNormal's 0-255 XYZ slots, lifting
    // the W byte verbatim from the source so the basis-sign / parity bit is
    // preserved. FPackedNormal's X/Y/Z accessors map byte -> [-1, 1] as
    // `byte / 127.5 - 1` (FPackedNormal.cs:14-16). We invert that mapping
    // here. The CUE4Parse-provided `FPackedNormal(FVector)` ctor has an
    // operator-precedence bug noted at FPackedNormal.cs:33 (`vector.X + 1 *
    // 127.5` parses as `vector.X + 127.5` and shifts compose wrongly), so we
    // pack by hand rather than route through it.
    private static FPackedNormal PackTangentWithSignByte(FVector unitVector, FPackedNormal sourcePackedForW)
    {
        // Clamp inputs to [-1, 1] to defend against floating-point drift on
        // the rotated vector (a unit normal pushed through TransformVectorNoScale
        // can drift by a few ulps, which after `* 127.5 + 127.5` could exceed
        // 255 and wrap on byte truncation).
        float clampedX = MathF.Min(1f, MathF.Max(-1f, unitVector.X));
        float clampedY = MathF.Min(1f, MathF.Max(-1f, unitVector.Y));
        float clampedZ = MathF.Min(1f, MathF.Max(-1f, unitVector.Z));

        uint xByte = (uint)Math.Clamp((int)MathF.Round((clampedX + 1f) * 127.5f), 0, 255);
        uint yByte = (uint)Math.Clamp((int)MathF.Round((clampedY + 1f) * 127.5f), 0, 255);
        uint zByte = (uint)Math.Clamp((int)MathF.Round((clampedZ + 1f) * 127.5f), 0, 255);
        // W byte: copy through the source's W slot verbatim — that bit is the
        // tangent-basis handedness flag and the spline deformation must not
        // perturb it.
        uint wByte = (sourcePackedForW.Data >> 24) & 0xFFu;

        uint packedData = xByte | (yByte << 8) | (zByte << 16) | (wByte << 24);
        return new FPackedNormal(packedData);
    }

    // System.Object.MemberwiseClone is `protected`; this thin reflection wrapper
    // is the canonical way to invoke it on instances of types we cannot
    // subclass (the CUE4Parse types are sealed-by-discipline and constructed by
    // deserialization). The reflected method is cached at first use so the
    // per-vertex inner loop pays no MethodInfo lookup cost — but here the
    // call is per-LOD (max 8 per source mesh) so caching is omitted for
    // clarity; the MethodInfo lookup is a few microseconds at scene scale.
    private static object CallMemberwiseClone(object target)
    {
        MethodInfo memberwiseClone = typeof(object).GetMethod(
            "MemberwiseClone",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return memberwiseClone.Invoke(target, null)!;
    }

    // Composite cache key for the deformed-mesh cache. The UniqueLightingGuid
    // folds in EVERY field that affects the slice math — SplineParams (hashed
    // via SHA-256), ForwardAxis (rotation column), SplineUpDir (basis X seed),
    // SplineBoundaryMin/Max (custom-boundary T-range), and bSmoothInterpRollScale
    // (lerp curve). Two splines therefore share one cloned UStaticMesh iff
    // their UniqueLightingGuid plus source mesh's LightingGuid match; any
    // deformation-affecting difference produces a distinct UniqueLightingGuid
    // and forces a fresh CPU bend. Earlier revision compared only
    // (SourceLightingGuid, SplineParamsHash, ForwardAxisBits) which silently
    // collided two splines that differed in SplineUpDir / boundary / smooth
    // flag — same params hash + same axis with a different up-dir would map
    // to one clone whose bend used the FIRST encounter's up-dir, contaminating
    // every later placement. UniqueLightingGuid is the authoritative key.
    private readonly struct DeformedMeshKey : IEquatable<DeformedMeshKey>
    {
        public readonly FGuid SourceLightingGuid;
        public readonly string SplineParamsHash;
        public readonly uint ForwardAxisBits;
        public readonly FGuid UniqueLightingGuid;

        public DeformedMeshKey(FGuid sourceLightingGuid, string splineParamsHash, uint forwardAxisBits, FGuid uniqueLightingGuid)
        {
            SourceLightingGuid = sourceLightingGuid;
            SplineParamsHash = splineParamsHash;
            ForwardAxisBits = forwardAxisBits;
            UniqueLightingGuid = uniqueLightingGuid;
        }

        public bool Equals(DeformedMeshKey other)
        {
            // SourceLightingGuid + UniqueLightingGuid are sufficient: identical
            // (source, fully-folded derived guid) tuples deform to byte-identical
            // clones, any difference in any deformation input shifts
            // UniqueLightingGuid via the XOR chain in BuildDeformedMeshKey.
            // SplineParamsHash and ForwardAxisBits remain stored for debugging
            // and as inputs to UniqueLightingGuid's derivation, but are NOT
            // compared because the derived guid already captures them.
            return SourceLightingGuid.Equals(other.SourceLightingGuid)
                && UniqueLightingGuid.Equals(other.UniqueLightingGuid);
        }

        public override bool Equals(object? obj) => obj is DeformedMeshKey other && Equals(other);

        public override int GetHashCode()
        {
            // Same authoritative pair: (source, unique). The other two fields
            // are derivable from UniqueLightingGuid by construction, so hashing
            // them adds nothing and risks asymmetry with Equals.
            return HashCode.Combine(SourceLightingGuid.GetHashCode(), UniqueLightingGuid.GetHashCode());
        }
    }
}
