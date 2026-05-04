using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader.GpuProgramType;
using Ruri.ShaderTools;

namespace AssetRipper.Export.Modules.Shaders.ShaderBlob;

public sealed class ShaderSubProgram
{
	/// <summary>
	/// 2019.1 and greater
	/// </summary>
	public static bool HasLocalKeywords(UnityVersion version) => version.GreaterThanOrEquals(2019);
	/// <summary>
	/// 5.5.0 and greater
	/// </summary>
	public static bool HasUAVParameters(UnityVersion version) => version.GreaterThanOrEquals(5, 5);
	/// <summary>
	/// 2017.2 and greater
	/// </summary>
	public static bool HasSamplerParameters(UnityVersion version) => version.GreaterThanOrEquals(2017, 1);
	/// <summary>
	/// 2017.3 and greater
	/// </summary>
	public static bool HasMultiSampled(UnityVersion version) => version.GreaterThanOrEquals(2017, 3);
	/// <summary>
	/// 5.5.0 and greater
	/// </summary>
	private static bool HasStatsTempRegister(UnityVersion version) => version.GreaterThanOrEquals(5, 5);
	/// <summary>
	/// 5.5.0 and greater
	/// </summary>
	private static bool IsAllParamArgs(UnityVersion version) => version.GreaterThanOrEquals(5, 5);
	/// <summary>
	/// 2017.3 and greater
	/// </summary>
	private static bool HasStructParameters(UnityVersion version) => version.GreaterThanOrEquals(2017, 3);
	/// <summary>
	/// 2018.2 and greater
	/// </summary>
	private static bool HasNewTextureParams(UnityVersion version) => version.GreaterThanOrEquals(2018, 2);
	/// <summary>
	/// 2021.2 and greater
	/// </summary>
	public static bool HasMergedKeywords(UnityVersion version) => version.GreaterThanOrEquals(2021, 2);

	public void Read(AssetReader reader, bool readProgramData, bool readParams)
	{
		// Consume + discard the blob version word. USCSandbox does the same (no validation), and
		// proprietary engines (AzurPromilia, EndField) emit values that don't match the vanilla
		// table — but the binary layout of the rest of the entry remains compatible.
		_ = reader.ReadInt32();

		if (readProgramData)
		{
			ReadProgramData(reader);
		}
		if (readParams)
		{
			ReadParameters(reader);
		}
	}

	private void ReadProgramData(AssetReader reader)
	{
		UnityVersion unityVersion = reader.AssetCollection.Version;

		ProgramType = reader.ReadInt32();
		StatsALU = reader.ReadInt32();
		StatsTEX = reader.ReadInt32();
		StatsFlow = reader.ReadInt32();
		if (HasStatsTempRegister(unityVersion))
		{
			StatsTempRegister = reader.ReadInt32();
		}

		if (HasMergedKeywords(unityVersion))
		{
			reader.ReadStringArray();
		}
		else
		{
			GlobalKeywords = reader.ReadStringArray();
			if (HasLocalKeywords(unityVersion))
			{
				LocalKeywords = reader.ReadStringArray();
			}
		}

		ProgramData = reader.ReadByteArray();
		reader.AlignStream();

		int sourceMap = reader.ReadInt32();
		int bindCount = reader.ReadInt32();
		ShaderBindChannel[] channels = new ShaderBindChannel[bindCount];
		for (int i = 0; i < bindCount; i++)
		{
			uint source = reader.ReadUInt32();
			VertexComponent target = (VertexComponent)reader.ReadUInt32();
			ShaderBindChannel channel = new ShaderBindChannel(source, target);
			channels[i] = channel;
			sourceMap |= 1 << (int)source;
		}
		BindChannels = new ParserBindChannels(channels, sourceMap);
	}

	private void ReadParameters(AssetReader reader)
	{
		UnityVersion unityVersion = reader.AssetCollection.Version;

		List<VectorParameter> vectors = new List<VectorParameter>();
		List<MatrixParameter> matrices = new List<MatrixParameter>();
		List<TextureParameter> textures = new List<TextureParameter>();
		List<VectorParameter> structVectors = new List<VectorParameter>();
		List<MatrixParameter> structMatrices = new List<MatrixParameter>();
		List<BufferBindingParameter> buffers = new List<BufferBindingParameter>();
		List<UAVParameter>? uavs = HasUAVParameters(unityVersion) ? new List<UAVParameter>() : null;
		List<SamplerParameter>? samplers = HasSamplerParameters(unityVersion) ? new List<SamplerParameter>() : null;
		List<BufferBindingParameter> constBindings = new List<BufferBindingParameter>();
		List<StructParameter> structs = new List<StructParameter>();

		int paramGroupCount = reader.ReadInt32();
		ConstantBufferParameters = new ConstantBufferParameter[paramGroupCount - 1];
		for (int i = 0; i < paramGroupCount; i++)
		{
			vectors.Clear();
			matrices.Clear();
			structs.Clear();

			string name = reader.ReadString();
			int usedSize = reader.ReadInt32();
			int paramCount = reader.ReadInt32();
			for (int j = 0; j < paramCount; j++)
			{
				string paramName = reader.ReadString();
				Ruri.ShaderTools.ShaderParamType paramType = (Ruri.ShaderTools.ShaderParamType)reader.ReadInt32();
				int rows = reader.ReadInt32();
				int columns = reader.ReadInt32();
				bool isMatrix = reader.ReadInt32() > 0;
				int arraySize = reader.ReadInt32();
				int index = reader.ReadInt32();

				if (isMatrix)
				{
					MatrixParameter matrix = IsAllParamArgs(unityVersion)
						? new MatrixParameter(paramName, paramType, index, arraySize, rows, columns)
						: new MatrixParameter(paramName, paramType, index, rows, columns);
					matrices.Add(matrix);
				}
				else
				{
					VectorParameter vector = IsAllParamArgs(unityVersion)
						? new VectorParameter(paramName, paramType, index, arraySize, columns)
						: new VectorParameter(paramName, paramType, index, columns);
					vectors.Add(vector);
				}
			}

			if (HasStructParameters(unityVersion))
			{
				int structCount = reader.ReadInt32();
				for (int j = 0; j < structCount; j++)
				{
					structVectors.Clear();
					structMatrices.Clear();

					string structName = reader.ReadString();
					int index = reader.ReadInt32();
					int arraySize = reader.ReadInt32();
					int structSize = reader.ReadInt32();

					int strucParamCount = reader.ReadInt32();
					for (int k = 0; k < strucParamCount; k++)
					{
						string paramName = reader.ReadString();
						paramName = $"{structName}.{paramName}";
						Ruri.ShaderTools.ShaderParamType paramType = (Ruri.ShaderTools.ShaderParamType)reader.ReadInt32();
						int rows = reader.ReadInt32();
						int columns = reader.ReadInt32();
						bool isMatrix = reader.ReadInt32() > 0;
						int vectorArraySize = reader.ReadInt32();
						int paramIndex = reader.ReadInt32();

						if (isMatrix)
						{
							MatrixParameter matrix = IsAllParamArgs(unityVersion)
								? new MatrixParameter(paramName, paramType, paramIndex, vectorArraySize, rows, columns)
								: new MatrixParameter(paramName, paramType, paramIndex, rows, columns);
							structMatrices.Add(matrix);
						}
						else
						{
							VectorParameter vector = IsAllParamArgs(unityVersion)
								? new VectorParameter(paramName, paramType, paramIndex, vectorArraySize, columns)
								: new VectorParameter(paramName, paramType, paramIndex, columns);
							structVectors.Add(vector);
						}
					}

					StructParameter @struct = new StructParameter(structName, index, arraySize, structSize, structVectors.ToArray(), structMatrices.ToArray());
					structs.Add(@struct);
				}
			}
			if (i == 0)
			{
				VectorParameters = vectors.ToArray();
				MatrixParameters = matrices.ToArray();
				StructParameters = structs.ToArray();
			}
			else
			{
				ConstantBufferParameter constBuffer = new ConstantBufferParameter(name, matrices.ToArray(), vectors.ToArray(), structs.ToArray(), usedSize);
				ConstantBufferParameters[i - 1] = constBuffer;
			}
		}

		int paramGroup2Count = reader.ReadInt32();
		for (int i = 0; i < paramGroup2Count; i++)
		{
			string name = reader.ReadString();
			int type = reader.ReadInt32();
			int index = reader.ReadInt32();
			int extraValue = reader.ReadInt32();

			if (type == 0)
			{
				TextureParameter texture;
				if (HasNewTextureParams(unityVersion))
				{
					uint textureExtraValue = reader.ReadUInt32();
					bool isMultiSampled = (textureExtraValue & 1) == 1;
					byte dimension = (byte)(textureExtraValue >> 1);
					int samplerIndex = extraValue;
					texture = new TextureParameter(name, index, dimension, samplerIndex, isMultiSampled);
				}
				else if (HasMultiSampled(unityVersion))
				{
					uint textureExtraValue = reader.ReadUInt32();
					bool isMultiSampled = textureExtraValue == 1;
					byte dimension = unchecked((byte)extraValue);
					int samplerIndex = extraValue >> 8;
					if (samplerIndex == 0xFFFFFF)
					{
						samplerIndex = -1;
					}

					texture = new TextureParameter(name, index, dimension, samplerIndex, isMultiSampled);
				}
				else
				{
					byte dimension = unchecked((byte)extraValue);
					int samplerIndex = extraValue >> 8;
					if (samplerIndex == 0xFFFFFF)
					{
						samplerIndex = -1;
					}

					texture = new TextureParameter(name, index, dimension, samplerIndex);
				}
				textures.Add(texture);
			}
			else if (type == 1)
			{
				BufferBindingParameter binding = new BufferBindingParameter(name, index);
				constBindings.Add(binding);
			}
			else if (type == 2)
			{
				BufferBindingParameter buffer = new BufferBindingParameter(name, index);
				buffers.Add(buffer);
			}
			else if (type == 3 && uavs is not null)
			{
				UAVParameter uav = new UAVParameter(name, index, extraValue);
				uavs.Add(uav);
			}
			else if (type == 4 && samplers is not null)
			{
				SamplerParameter sampler = new SamplerParameter((uint)extraValue, index);
				samplers.Add(sampler);
			}
			else
			{
				throw new Exception($"Unupported parameter type {type}");
			}
		}
		TextureParameters = textures.ToArray();
		BufferParameters = buffers.ToArray();
		if (uavs is not null)
		{
			UAVParameters = uavs.ToArray();
		}

		if (samplers is not null)
		{
			SamplerParameters = samplers.ToArray();
		}

		BufferBindingParameters = constBindings.ToArray();
		if (HasStructParameters(unityVersion))
		{
			StructParameters = structs.ToArray();
		}
	}

	public ShaderGpuProgramType GetProgramType(UnityVersion version)
	{
		if (ShaderGpuProgramTypeExtensions.GpuProgramType55Relevant(version))
		{
			return ((ShaderGpuProgramType55)ProgramType).ToGpuProgramType();
		}
		else
		{
			return ((ShaderGpuProgramType53)ProgramType).ToGpuProgramType();
		}
	}

	public int ProgramType { get; set; }
	public int StatsALU { get; set; }
	public int StatsTEX { get; set; }
	public int StatsFlow { get; set; }
	public int StatsTempRegister { get; set; }
	public string[] GlobalKeywords { get; set; } = Array.Empty<string>();
	public string[] LocalKeywords { get; set; } = Array.Empty<string>();
	public byte[] ProgramData { get; set; } = Array.Empty<byte>();
	public VectorParameter[] VectorParameters { get; set; } = Array.Empty<VectorParameter>();
	public MatrixParameter[] MatrixParameters { get; set; } = Array.Empty<MatrixParameter>();
	public TextureParameter[] TextureParameters { get; set; } = Array.Empty<TextureParameter>();
	public BufferBindingParameter[] BufferParameters { get; set; } = Array.Empty<BufferBindingParameter>();
	public UAVParameter[] UAVParameters { get; set; } = Array.Empty<UAVParameter>();
	public SamplerParameter[] SamplerParameters { get; set; } = Array.Empty<SamplerParameter>();
	public ConstantBufferParameter[] ConstantBufferParameters { get; set; } = Array.Empty<ConstantBufferParameter>();
	public BufferBindingParameter[] BufferBindingParameters { get; set; } = Array.Empty<BufferBindingParameter>();
	public StructParameter[] StructParameters { get; set; } = Array.Empty<StructParameter>();
	public ParserBindChannels BindChannels { get; set; } = new();
}
