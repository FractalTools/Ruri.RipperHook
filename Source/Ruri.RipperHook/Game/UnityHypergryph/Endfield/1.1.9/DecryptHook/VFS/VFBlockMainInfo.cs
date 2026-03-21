using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Ruri.RipperHook.Crypto;

namespace Ruri.RipperHook.Endfield.VFS;

public class VFBlockMainInfo
{
    public int version;
    public string groupCfgName;
    public long groupCfgHashName;
    public int groupFileInfoNum;
    public long groupChunksLength;
    public EVFSBlockType blockType;
    public FVFBlockChunkInfo[] allChunks;
    public int codeVersion;

    public VFBlockMainInfo(byte[] bytes, int startOffset = 0, bool verifyCrc = true)
    {
        if (verifyCrc)
        {
            int dataLength = bytes.Length - startOffset - sizeof(int);
            if (dataLength <= 0)
                throw new InvalidDataException("Block data too short for CRC verification");

            int expectedCrc = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(startOffset + dataLength));
            int actualCrc = Crc32.Calculate(bytes, startOffset, dataLength);

            if (expectedCrc != actualCrc)
                throw new InvalidDataException($"CRC mismatch: expected 0x{expectedCrc:X8}, got 0x{actualCrc:X8}");
        }

        int offset = startOffset;

        int rawVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(int);

        if (rawVersion < 11)
        {
            codeVersion = rawVersion;
            version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(int);
        }
        else
        {
            codeVersion = 3;
            version = rawVersion;
        }

        ushort groupCfgNameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(ushort);

        groupCfgName = Encoding.UTF8.GetString(bytes.AsSpan(offset, groupCfgNameLength));
        offset += groupCfgNameLength;

        groupCfgHashName = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(long);

        groupFileInfoNum = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(int);

        groupChunksLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
        offset += sizeof(long);

        blockType = (EVFSBlockType)bytes[offset++];

        var chunkCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        allChunks = new FVFBlockChunkInfo[chunkCount];
        offset += sizeof(int);

        for (int ci = 0; ci < chunkCount; ci++)
        {
            ref var chunk = ref allChunks[ci];

            chunk.md5Name = BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(offset));
            offset += Marshal.SizeOf<UInt128>();

            chunk.contentMD5 = BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(offset));
            offset += Marshal.SizeOf<UInt128>();

            chunk.length = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
            offset += sizeof(long);

            chunk.blockType = (EVFSBlockType)bytes[offset++];

            if (codeVersion > 3)
            {
                chunk.mainTag = (EVFSFileTag)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(int);
            }

            var fileCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
            chunk.files = new FVFBlockFileInfo[fileCount];
            offset += sizeof(int);

            for (int fi = 0; fi < fileCount; fi++)
            {
                ref var file = ref chunk.files[fi];

                int fileNameOffset = offset;
                ushort fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(ushort);

                offset += fileNameLength;

                file.fileNameHash = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(long);

                file.fileChunkMD5Name = BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(offset));
                offset += Marshal.SizeOf<UInt128>();

                file.fileDataMD5 = BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(offset));
                offset += Marshal.SizeOf<UInt128>();

                file.offset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(long);

                file.len = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
                offset += sizeof(long);

                file.blockType = (EVFSBlockType)bytes[offset++];

                file.bUseEncrypt = Convert.ToBoolean(bytes[offset++]);

                if (file.bUseEncrypt)
                {
                    file.ivSeed = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
                    offset += sizeof(long);
                }

                if (codeVersion > 3)
                {
                    offset += sizeof(int); // fileTag
                }

                file.fileName = Encoding.UTF8.GetString(bytes.AsSpan(fileNameOffset + sizeof(ushort), fileNameLength));
            }
        }
    }
}
