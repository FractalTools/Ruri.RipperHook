using AssetRipper.Import.Logging;
using Ruri.RipperHook.Crypto;

namespace Ruri.RipperHook.Endfield.VFS;

public static class VFSBlockReader
{
    private static readonly byte[] ChaChaKey = Convert.FromBase64String(VFSDefine.CHACHA_KEY);

    /// <summary>
    /// Decrypt and parse .blc metadata file.
    /// </summary>
    public static VFBlockMainInfo ReadBlockMetadata(string blcPath)
    {
        var blockFile = File.ReadAllBytes(blcPath);

        // First 12 bytes = ChaCha20 nonce
        byte[] nonce = new byte[VFSDefine.BLOCK_HEAD_LEN];
        Buffer.BlockCopy(blockFile, 0, nonce, 0, nonce.Length);

        // Decrypt the rest
        using var chacha = new CSChaCha20(ChaChaKey, nonce, 1);
        var decrypted = chacha.DecryptBytes(blockFile[VFSDefine.BLOCK_HEAD_LEN..]);
        Buffer.BlockCopy(decrypted, 0, blockFile, VFSDefine.BLOCK_HEAD_LEN, decrypted.Length);

        return new VFBlockMainInfo(blockFile, VFSDefine.BLOCK_HEAD_LEN);
    }

    /// <summary>
    /// Extract a single file from a .chk chunk, decrypting if needed.
    /// </summary>
    public static byte[] ExtractFile(Stream chunkStream, FVFBlockFileInfo fileInfo)
    {
        chunkStream.Seek(fileInfo.offset, SeekOrigin.Begin);
        var data = new byte[fileInfo.len];
        chunkStream.ReadExactly(data, 0, data.Length);

        if (fileInfo.bUseEncrypt)
        {
            byte[] fileNonce = new byte[VFSDefine.BLOCK_HEAD_LEN];
            Buffer.BlockCopy(BitConverter.GetBytes(VFSDefine.VFS_PROTO_VERSION), 0, fileNonce, 0, sizeof(int));
            Buffer.BlockCopy(BitConverter.GetBytes(fileInfo.ivSeed), 0, fileNonce, sizeof(int), sizeof(long));

            using var chacha = new CSChaCha20(ChaChaKey, fileNonce, 1);
            data = chacha.DecryptBytes(data);
        }

        return data;
    }

    /// <summary>
    /// Extract all bundle files from a .chk, using .blc metadata.
    /// Returns (fileName, fileData) pairs for each file in the matching chunk.
    /// </summary>
    public static IEnumerable<(string name, byte[] data)> ExtractChunkFiles(
        string chkPath, VFBlockMainInfo blockInfo)
    {
        // Match .chk filename to chunk md5Name
        string chkName = Path.GetFileNameWithoutExtension(chkPath).ToUpperInvariant();

        FVFBlockChunkInfo? matchedChunk = null;
        foreach (var chunk in blockInfo.allChunks)
        {
            string chunkHex = Convert.ToHexString(BitConverter.GetBytes(chunk.md5Name));
            if (chunkHex.Equals(chkName, StringComparison.OrdinalIgnoreCase))
            {
                matchedChunk = chunk;
                break;
            }
        }

        if (matchedChunk == null)
        {
            Logger.Warning($"[VFS] No chunk in .blc matches .chk filename: {chkName}");
            yield break;
        }

        var chunk_ = matchedChunk.Value;
        Logger.Info($"[VFS] Extracting {chunk_.files.Length} files from chunk {chkName}");

        using var chunkFs = File.OpenRead(chkPath);
        foreach (var file in chunk_.files)
        {
            byte[] fileData;
            try
            {
                fileData = ExtractFile(chunkFs, file);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[VFS] Failed to extract '{file.fileName}': {ex.Message}");
                continue;
            }

            yield return (file.fileName, fileData);
        }
    }
}
