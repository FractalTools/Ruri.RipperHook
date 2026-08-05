using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

// Routes USplineMeshComponent placements through GlbSceneContext.AddSplineMesh
// so they land in the SAME .glb part files as the straight static meshes.
//
// The bend itself belongs to CUE4Parse: MeshLodDto.FromStaticMesh applies
// spline.CalcSliceTransform per vertex while it walks the LOD, so geometry
// bytes match the stock spline-mesh export. What this exporter owns is the
// bend's IDENTITY — the share key that decides whether two placements can
// reuse one MeshBuilder (see BuildDeformedMeshKey).
public sealed class SplineMeshComponentExporter : IComponentExporter
{
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

        // OverrideMaterials handling mirrors the FModel Renderer path
        // through StaticMeshComponentExporter.BuildOverrideMaterialLists. Doing
        // it identically here keeps the mesh-share key contributions consistent
        // across the two exporters: an overridden bent mesh + a bent mesh with
        // no overrides yield distinct MeshBuilders inside GlbSceneContext,
        // exactly like the straight-static path does for ISM placements.
        BuildOverrideMaterialLists(
            spline,
            out IReadOnlyList<UMaterialInterface?> overrideMaterials,
            out IReadOnlyList<string> overrideMaterialPathNames);

        // The deformed positions are expressed in the spline component's
        // LOCAL space (the CalcSliceTransform output lives in component space
        // because both splinePos and the basis vectors are derived from
        // SplineParams.*, which are defined in component space). So the
        // placement's world transform (which already folds the AttachParent
        // chain via SceneTransform.CalculateTransform) is handed straight
        // through SceneTransform.NodeMatrix (N = W, FModel's placement matrix)
        // to put the bent mesh exactly where the preview puts the straight mesh
        // of a non-spline component.
        context.AddSplineMesh(
            spline,
            sourceMesh.Name,
            key.UniqueLightingGuid,
            overrideMaterials,
            overrideMaterialPathNames,
            SceneTransform.NodeMatrix(placed.WorldTransform));
    }

    // Mirror of StaticMeshComponentExporter.BuildOverrideMaterialLists
    // (kept duplicated rather than refactored into the shared file because the
    // static-mesh exporter file is owned by another cell). The duplication MUST
    // stay byte-exact — any divergence in the cache-signature derivation would
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
