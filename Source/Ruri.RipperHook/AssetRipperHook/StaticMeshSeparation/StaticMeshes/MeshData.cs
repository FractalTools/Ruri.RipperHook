using AssetRipper.Assets.Generics;
using AssetRipper.Numerics;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Enums;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using System.Numerics;
using Array = System.Array;

namespace Ruri.RipperHook.AR
{
	internal readonly record struct MeshData(
		Vector3[] Vertices,
		Vector3[]? Normals,
		Vector4[]? Tangents,
		ColorFloat[]? Colors,
		BoneWeight4[]? Skin,
		Vector2[]? UV0,
		Vector2[]? UV1,
		Vector2[]? UV2,
		Vector2[]? UV3,
		Vector2[]? UV4,
		Vector2[]? UV5,
		Vector2[]? UV6,
		Vector2[]? UV7,
		Matrix4x4[]? BindPose,
		uint[] ProcessedIndexBuffer,
		SubMeshData[] SubMeshes)
	{
		public static bool TryMakeFromMesh(IMesh mesh, out MeshData meshData)
		{
			mesh.ReadData(
				out Vector3[]? vertices,
				out Vector3[]? normals,
				out Vector4[]? tangents,
				out ColorFloat[]? colors,
				out Vector2[]? uv0,
				out Vector2[]? uv1,
				out Vector2[]? uv2,
				out Vector2[]? uv3,
				out Vector2[]? uv4,
				out Vector2[]? uv5,
				out Vector2[]? uv6,
				out Vector2[]? uv7,
				out BoneWeight4[]? skin,
				out Matrix4x4[]? bindpose,
                out uint[] processedIndexBuffer);

			SubMeshData[] subMeshes = GetSubMeshArray(mesh);

			if (vertices is null)
			{
				meshData = default;
				return false;
			}
			else
			{
				meshData = new MeshData(vertices, normals, tangents, colors, skin, uv0, uv1, uv2, uv3, uv4, uv5, uv6, uv7, bindpose, processedIndexBuffer, subMeshes);
				return true;
			}
		}

		internal MeshData DeepClone()
		{
			return new()
			{
				Vertices = DuplicateArray(Vertices),
				Normals = DuplicateArray(Normals),
				Tangents = DuplicateArray(Tangents),
				Colors = DuplicateArray(Colors),
				Skin = DuplicateArray(Skin),
				UV0 = DuplicateArray(UV0),
				UV1 = DuplicateArray(UV1),
				UV2 = DuplicateArray(UV2),
				UV3 = DuplicateArray(UV3),
				UV4 = DuplicateArray(UV4),
				UV5 = DuplicateArray(UV5),
				UV6 = DuplicateArray(UV6),
				UV7 = DuplicateArray(UV7),
				BindPose = DuplicateArray(BindPose),
				ProcessedIndexBuffer = DuplicateArray(ProcessedIndexBuffer),
				SubMeshes = DuplicateArray(SubMeshes),
			};
		}

		[return: NotNullIfNotNull(nameof(array))]
		private static T[]? DuplicateArray<T>(T[]? array)
		{
			if (array is null)
			{
				return null;
			}
			else if (array.Length == 0)
			{
				return Array.Empty<T>();
			}
			else
			{
				T[] arrayCopy = new T[array.Length];
				Array.Copy(array, arrayCopy, arrayCopy.Length);
				return arrayCopy;
			}
		}

		private static SubMeshData[] GetSubMeshArray(IMesh mesh)
		{
			AccessListBase<ISubMesh> list = mesh.SubMeshes;
			if (list.Count == 0)
			{
				return Array.Empty<SubMeshData>();
			}
			else
			{
				SubMeshData[] array = new SubMeshData[list.Count];
				for (int i = 0; i < list.Count; i++)
				{
					array[i] = SubMeshData.Create(list[i], mesh.IndexFormatE);
				}
				return array;
			}
		}

		public MeshData MakeComparableMeshData()
		{
			if (IsComparable)
			{
				return this;
			}

			int count = 0;
			foreach (SubMeshData subMesh in SubMeshes)
			{
				count += subMesh.IndexCount;
			}
			Vector3[] vertices = new Vector3[count];
			Vector3[]? normals = Normals.IsNullOrEmpty() ? null : new Vector3[count];
			Vector4[]? tangents = Tangents.IsNullOrEmpty() ? null : new Vector4[count];
			ColorFloat[]? colors = Colors.IsNullOrEmpty() ? null : new ColorFloat[count];
			BoneWeight4[]? skin = Skin.IsNullOrEmpty() ? null : new BoneWeight4[count];
			Vector2[]? uv0 = UV0.IsNullOrEmpty() ? null : new Vector2[count];
			Vector2[]? uv1 = UV1.IsNullOrEmpty() ? null : new Vector2[count];
			Vector2[]? uv2 = UV2.IsNullOrEmpty() ? null : new Vector2[count];
			Vector2[]? uv3 = UV3.IsNullOrEmpty() ? null : new Vector2[count];
			Vector2[]? uv4 = UV4.IsNullOrEmpty() ? null : new Vector2[count];
			Vector2[]? uv5 = UV5.IsNullOrEmpty() ? null : new Vector2[count];
			Vector2[]? uv6 = UV6.IsNullOrEmpty() ? null : new Vector2[count];
			Vector2[]? uv7 = UV7.IsNullOrEmpty() ? null : new Vector2[count];
			Matrix4x4[]? bindpose = BindPose.IsNullOrEmpty() ? null : new Matrix4x4[count];
			uint[] processedIndexBuffer = new uint[count];
			SubMeshData[] subMeshes = new SubMeshData[SubMeshes.Length];
			int offset = 0;
			for (int i = 0; i < SubMeshes.Length; i++)
			{
				SubMeshData subMesh = SubMeshes[i];
				for (int j = 0; j < subMesh.IndexCount; j++)
				{
					int destination = offset + j;
					int index = (int)(ProcessedIndexBuffer[subMesh.FirstIndex + j] + subMesh.BaseVertex);
					vertices[destination] = Vertices[index];
					SetUnlessNull(normals, destination, Normals, index);
					SetUnlessNull(tangents, destination, Tangents, index);
					SetUnlessNull(colors, destination, Colors, index);
					SetUnlessNull(skin, destination, Skin, index);
					SetUnlessNull(uv0, destination, UV0, index);
					SetUnlessNull(uv1, destination, UV1, index);
					SetUnlessNull(uv2, destination, UV2, index);
					SetUnlessNull(uv3, destination, UV3, index);
					SetUnlessNull(uv4, destination, UV4, index);
					SetUnlessNull(uv5, destination, UV5, index);
					SetUnlessNull(uv6, destination, UV6, index);
					SetUnlessNull(uv7, destination, UV7, index);
					SetUnlessNull(bindpose, destination, BindPose, index);
					processedIndexBuffer[destination] = (uint)destination;
				}
				subMesh.FirstIndex = offset;
				subMesh.FirstVertex = offset;
				subMesh.VertexCount = subMesh.IndexCount;
				subMesh.BaseVertex = 0;
				subMesh.LocalBounds = Bounds.CalculateFromVertexArray(new ReadOnlySpan<Vector3>(vertices, offset, subMesh.IndexCount));
				subMeshes[i] = subMesh;
				offset += subMesh.IndexCount;
			}

			return new MeshData(vertices, normals, tangents, colors, skin, uv0, uv1, uv2, uv3, uv4, uv5, uv6, uv7, bindpose, processedIndexBuffer, subMeshes);
		}

		private static void SetUnlessNull<T>(T[]? array1, int index1, T[]? array2, int index2)
		{
			if (array1 is not null)
			{
				array1[index1] = array2![index2];			}
		}

		public bool IsComparable
		{
			get
			{
				int offset = 0;
				foreach (SubMeshData subMesh in SubMeshes)
				{
					if (subMesh.BaseVertex != 0 || subMesh.FirstIndex != offset || subMesh.FirstVertex != offset
						|| subMesh.VertexCount != subMesh.IndexCount)
					{
						return false;
					}
					offset += subMesh.IndexCount;
				}
				if (offset != ProcessedIndexBuffer.Length || offset != Vertices.Length)
				{
					return false;
				}
				for (uint i = 0; i < ProcessedIndexBuffer.Length; i++)
				{
					if (i != ProcessedIndexBuffer[i])
					{
						return false;
					}
				}
				return true;
			}
		}

		public IndexFormat GetIndexFormat()
		{
			if (Vertices.Length > ushort.MaxValue)
			{
				return IndexFormat.UInt32;
			}
			else
			{
				return IndexFormat.UInt16;
			}
		}
	}
}
