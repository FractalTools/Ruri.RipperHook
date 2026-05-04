using AssetRipper.Numerics;
using AssetRipper.SourceGenerated.Extensions;
using TkVector2 = OpenTK.Mathematics.Vector2;
using TkVector3 = OpenTK.Mathematics.Vector3;
using TkVector4 = OpenTK.Mathematics.Vector4;

namespace Ruri.RipperHook.GUI.Services;

internal sealed class MeshPreviewPayload
{
	public required TkVector3[] Vertices { get; init; }
	public required TkVector3[] Normals { get; init; }
	public required TkVector4[] Colors { get; init; }
	public required TkVector2[] Uv0 { get; init; }
	public required int[] Indices { get; init; }
	public required SubMeshPreview[] SubMeshes { get; init; }
	public MeshTexturePreview[] Textures { get; set; } = [];

	public static MeshPreviewPayload FromMeshData(MeshData meshData)
	{
		TkVector3[] vertices = meshData.Vertices.Select(ToOpenTk).ToArray();
		int[] indices = meshData.ProcessedIndexBuffer.Select(static i => unchecked((int)i)).ToArray();
		TkVector3[] normals = BuildNormals(meshData, vertices, indices);
		TkVector4[] colors = BuildColors(meshData, vertices.Length);
		TkVector2[] uv0 = BuildUv0(meshData, vertices.Length);
		SubMeshPreview[] subMeshes = meshData.SubMeshes
			.Where(static sm => sm.IndexCount > 0)
			.Select(static sm => new SubMeshPreview(sm.FirstIndex, sm.IndexCount))
			.ToArray();

		if (subMeshes.Length == 0)
		{
			subMeshes = [new SubMeshPreview(0, indices.Length)];
		}

		return new MeshPreviewPayload
		{
			Vertices = vertices,
			Normals = normals,
			Colors = colors,
			Uv0 = uv0,
			Indices = indices,
			SubMeshes = subMeshes,
		};
	}

	private static TkVector3[] BuildNormals(MeshData meshData, TkVector3[] vertices, int[] indices)
	{
		if (meshData.HasNormals)
		{
			return meshData.Normals!.Select(ToOpenTk).ToArray();
		}

		TkVector3[] generated = new TkVector3[vertices.Length];
		for (int i = 0; i + 2 < indices.Length; i += 3)
		{
			int ia = indices[i];
			int ib = indices[i + 1];
			int ic = indices[i + 2];
			if ((uint)ia >= (uint)vertices.Length || (uint)ib >= (uint)vertices.Length || (uint)ic >= (uint)vertices.Length)
			{
				continue;
			}

			TkVector3 edge1 = vertices[ib] - vertices[ia];
			TkVector3 edge2 = vertices[ic] - vertices[ia];
			TkVector3 normal = TkVector3.Cross(edge1, edge2);
			if (normal.LengthSquared > 1e-12f)
			{
				normal.Normalize();
				generated[ia] += normal;
				generated[ib] += normal;
				generated[ic] += normal;
			}
		}

		for (int i = 0; i < generated.Length; i++)
		{
			if (generated[i].LengthSquared <= 1e-12f)
			{
				generated[i] = TkVector3.UnitY;
			}
			else
			{
				generated[i].Normalize();
			}
		}

		return generated;
	}

	private static TkVector4[] BuildColors(MeshData meshData, int vertexCount)
	{
		if (meshData.HasColors)
		{
			return meshData.Colors!
				.Select(static c => new TkVector4(c.R, c.G, c.B, c.A))
				.ToArray();
		}

		return Enumerable.Repeat(new TkVector4(0.85f, 0.85f, 0.9f, 1f), vertexCount).ToArray();
	}

	private static TkVector2[] BuildUv0(MeshData meshData, int vertexCount)
	{
		if (meshData.HasUV0)
		{
			return meshData.UV0!
				.Select(static uv => new TkVector2(uv.X, 1f - uv.Y))
				.ToArray();
		}

		return Enumerable.Repeat(TkVector2.Zero, vertexCount).ToArray();
	}

	private static TkVector3 ToOpenTk(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}

internal readonly record struct SubMeshPreview(int StartIndex, int Count);

internal readonly record struct MeshTexturePreview(int StartIndex, int Count, byte[] PngData);
