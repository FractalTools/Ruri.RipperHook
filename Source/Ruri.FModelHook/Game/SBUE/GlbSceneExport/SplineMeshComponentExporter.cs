using System;
using System.Collections.Generic;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component.SplineMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;

namespace Ruri.FModelHook.Game.SBUE.GlbSceneExport;

public sealed class SplineMeshComponentExporter : IComponentExporter
{
    public bool CanExport(UObject component) => component is USplineMeshComponent;

    public void Export(in PlacedComponent placed, GlbSceneContext context)
    {
        if (placed.Component is not USplineMeshComponent spline) return;

        UStaticMesh? sourceMesh = spline.GetStaticMesh().Load<UStaticMesh>();
        if (sourceMesh == null || sourceMesh.Materials.Length < 1)
        {
            return;
        }

        DeformedMeshKey key = BuildDeformedMeshKey(sourceMesh, spline);

        BuildOverrideMaterialLists(
            spline,
            out IReadOnlyList<UMaterialInterface?> overrideMaterials,
            out IReadOnlyList<string> overrideMaterialPathNames);

        context.AddSplineMesh(
            spline,
            sourceMesh.Name,
            key.UniqueLightingGuid,
            overrideMaterials,
            overrideMaterialPathNames,
            SceneTransform.NodeMatrix(placed.WorldTransform));
    }

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

    private static DeformedMeshKey BuildDeformedMeshKey(UStaticMesh sourceMesh, USplineMeshComponent spline)
    {
        string splineParamsHashHex = spline.SplineParams.GetSHAHash();
        Span<uint> hashWords = stackalloc uint[4];
        for (int hashWordIndex = 0; hashWordIndex < 4; hashWordIndex++)
        {
            hashWords[hashWordIndex] = uint.Parse(
                splineParamsHashHex.AsSpan(hashWordIndex * 8, 8),
                System.Globalization.NumberStyles.HexNumber);
        }

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

    private static uint HashFloatTriple(float a, float b, float c)
    {
        return unchecked((uint)HashCode.Combine(a, b, c));
    }

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
            return SourceLightingGuid.Equals(other.SourceLightingGuid)
                && UniqueLightingGuid.Equals(other.UniqueLightingGuid);
        }

        public override bool Equals(object? obj) => obj is DeformedMeshKey other && Equals(other);

        public override int GetHashCode()
        {
            return HashCode.Combine(SourceLightingGuid.GetHashCode(), UniqueLightingGuid.GetHashCode());
        }
    }
}
