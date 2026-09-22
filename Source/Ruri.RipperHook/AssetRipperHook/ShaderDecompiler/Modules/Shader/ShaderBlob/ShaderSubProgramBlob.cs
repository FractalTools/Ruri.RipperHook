using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;

namespace AssetRipper.Export.Modules.Shaders.ShaderBlob;

public sealed class ShaderSubProgramBlob
{
	public void Read(AssetCollection shaderCollection, byte[] compressedBlob, uint offset, uint compressedLength, uint decompressedLength)
	{
		m_shaderCollection = shaderCollection;
		ReadBlob(compressedBlob, offset, compressedLength, decompressedLength, 0);
	}

	public void Read(AssetCollection shaderCollection, byte[] compressedBlob, AssetList<uint> offsets, AssetList<uint> compressedLengths, AssetList<uint> decompressedLengths)
	{
		m_shaderCollection = shaderCollection;
		for (int i = 0; i < offsets.Count; i++)
		{
			ReadBlob(compressedBlob, offsets[i], compressedLengths[i], decompressedLengths[i], i);
		}
	}

	private void ReadBlob(byte[] compressedBlob, uint offset, uint compressedLength, uint decompressedLength, int segment)
	{
		while (m_decompressedBlobSegments.Count < segment + 1) { m_decompressedBlobSegments.Add([]); }
		m_decompressedBlobSegments[segment] = DecompressedBlob.DecompressBlob(compressedBlob, offset, compressedLength, decompressedLength);

		if (segment == 0)
		{
			using MemoryStream blobMem = new MemoryStream(m_decompressedBlobSegments[segment]);
			using AssetReader blobReader = new AssetReader(blobMem, m_shaderCollection);
			Entries = ReadAssetArray(blobReader);
			m_cachedSubPrograms.Clear();
		}
	}

	private static ShaderSubProgramEntry[] ReadAssetArray(AssetReader reader)
	{
		int count = reader.ReadInt32();

		ShaderSubProgramEntry[] array = CreateAndInitializeArray<ShaderSubProgramEntry>(count);
		for (int i = 0; i < count; i++)
		{
			array[i].Read(reader);
		}
		if (reader.IsAlignArray)
		{
			reader.AlignStream();
		}
		return array;
	}

	private static T[] CreateAndInitializeArray<T>(int length) where T : new()
	{
		ArgumentOutOfRangeException.ThrowIfNegative(length);

		if (length == 0)
		{
			return [];
		}

		T[] array = new T[length];
		for (int i = 0; i < length; i++)
		{
			array[i] = new();
		}
		return array;
	}


	public ShaderSubProgram GetSubProgram(uint blobIndex)
	{
		if (m_cachedSubPrograms.TryGetValue((blobIndex, blobIndex), out ShaderSubProgram? subProgram))
		{
			return subProgram;
		}

		subProgram = new ShaderSubProgram();
		ReadEntry(blobIndex, subProgram, readProgramData: true, readParams: true);

		m_cachedSubPrograms.TryAdd((blobIndex, blobIndex), subProgram);
		return subProgram;
	}

	public ShaderSubProgram GetSubProgram(uint blobIndex, uint paramBlobIndex)
	{
		if (m_cachedSubPrograms.TryGetValue((blobIndex, paramBlobIndex), out ShaderSubProgram? subProgram))
		{
			return subProgram;
		}

		subProgram = new ShaderSubProgram();
		ReadEntry(blobIndex, subProgram, readProgramData: true, readParams: false);
		ReadEntry(paramBlobIndex, subProgram, readProgramData: false, readParams: true);

		m_cachedSubPrograms.TryAdd((blobIndex, paramBlobIndex), subProgram);
		return subProgram;
	}

	public int LastDanglingIndexCount { get; private set; }

	private void ReadEntry(uint index, ShaderSubProgram subProgram, bool readProgramData, bool readParams)
	{
		if (index >= Entries.Length)
		{
			LastDanglingIndexCount++;
			return;
		}

		ShaderSubProgramEntry entry = Entries[index];
		byte[] segmentBytes = m_decompressedBlobSegments[entry.Segment];
		using MemoryStream entryMem = new MemoryStream(segmentBytes, entry.Offset, entry.Length, writable: false);
		using AssetReader entryReader = new AssetReader(entryMem, m_shaderCollection);

		if (!readProgramData && readParams)
		{
			try
			{
				subProgram.Read(entryReader, readProgramData, readParams);
			}
			catch
			{
			}
			return;
		}

		try
		{
			subProgram.Read(entryReader, readProgramData, readParams);
		}
		catch (Exception ex)
		{
			UnreadableProgramDataCount++;
			if (Environment.GetEnvironmentVariable("RURI_SHADER_BLOB_DEBUG") == "1")
			{
				Console.Error.WriteLine($"[BlobDebug] entry {index}/{Entries.Length} seg={entry.Segment} off={entry.Offset} len={entry.Length} not a code entry: {ex.Message}");
				Console.Error.WriteLine("[BlobDebug]   head=" + BitConverter.ToString(segmentBytes, entry.Offset, Math.Min(48, entry.Length)));
			}
		}
	}

	public int UnreadableProgramDataCount { get; private set; }

	public ShaderSubProgramEntry[] Entries { get; set; } = [];

	private AssetCollection m_shaderCollection;
	private List<byte[]> m_decompressedBlobSegments = [];
	private readonly Dictionary<(uint, uint), ShaderSubProgram> m_cachedSubPrograms = new();

	public const string GpuProgramIndexName = "GpuProgramIndex";
}
