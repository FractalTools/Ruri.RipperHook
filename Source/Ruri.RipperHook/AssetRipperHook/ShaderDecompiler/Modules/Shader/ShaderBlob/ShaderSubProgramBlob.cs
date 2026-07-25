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

	/// <summary>
	/// Creates an array with non-null elements
	/// </summary>
	/// <typeparam name="T">The type of the array elements</typeparam>
	/// <param name="length">The length of the array</param>
	/// <returns>A new array of the specified length and type</returns>
	/// <exception cref="ArgumentOutOfRangeException">Length less than zero</exception>
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

	/// <summary>
	/// Open a fresh reader scoped to a single blob entry. Each entry can live in its own segment,
	/// and entry sizes are not necessarily aligned with the standard parser's read width — so we
	/// build a window-stream rather than seeking inside a shared per-segment stream. Mirrors
	/// USCSandbox's <c>BlobManager.GetRawEntry</c>+fresh-reader pattern.
	///
	/// Reads tolerate parameter-side parse errors: when <paramref name="readParams"/> is true and
	/// the standard layout doesn't apply (e.g. proprietary engine packed the param blob in its
	/// own format) we swallow the exception and leave the params empty. Program data must still
	/// parse; if that throws we let it propagate so the caller sees the real failure.
	/// </summary>
	// Surfaces in the per-shader summary so the caller can tell "we silently skipped a sub-
	// program" from "the shader had no sub-programs at all". Gets reset per shader in the
	// exporter when caching is bypassed.
	public int LastDanglingIndexCount { get; private set; }

	private void ReadEntry(uint index, ShaderSubProgram subProgram, bool readProgramData, bool readParams)
	{
		// PlayerSubPrograms / ParameterBlobIndices are shared across platforms (and across LOD
		// SubShaders) in the structured shader metadata, but the per-platform consolidated blob
		// entry table is specific to one LOD's slice of one platform. When we pick e.g. Vulkan
		// for the highest-LOD SubShader, BlobIndex / paramBlobIndex from passes belonging to
		// other LODs may dangle past Entries.Length. Skip silently and count how many we lost so
		// the caller can sanity-check the surviving sub-programs.
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
				// Param blob entry didn't conform to the vanilla Unity layout. The structured
				// SerializedSubProgram.Parameters set (from AssetRipper-side metadata) is still
				// available downstream, so we degrade gracefully rather than tank the shader.
			}
			return;
		}

		// A blob index that resolves to a PARAMETER-only entry rather than a code entry is not a
		// parse bug to propagate — the entry table mixes both kinds, and a proprietary engine's
		// per-platform index spaces don't have to agree about which slots hold which (EndField's
		// Vulkan table is contiguous where its D3D11 table steps by two, so a stale index can land
		// on the wrong kind). Such an entry starts straight into the parameter block, so the
		// keyword-array read walks off the end. Leave the sub-program empty and count it: the
		// caller already skips sub-programs with no program data, and the count is what tells
		// "this variant had nothing to decompile" apart from "we lost it silently".
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

	/// <summary>
	/// How many blob indices resolved to an entry whose program data could not be parsed (see
	/// <see cref="ReadEntry"/>). Non-zero is not automatically wrong — it means those variants
	/// contributed nothing — but a jump in it is how a mis-resolved index space shows up.
	/// </summary>
	public int UnreadableProgramDataCount { get; private set; }

	public ShaderSubProgramEntry[] Entries { get; set; } = [];

	private AssetCollection m_shaderCollection;
	private List<byte[]> m_decompressedBlobSegments = [];
	private readonly Dictionary<(uint, uint), ShaderSubProgram> m_cachedSubPrograms = new();

	public const string GpuProgramIndexName = "GpuProgramIndex";
}
