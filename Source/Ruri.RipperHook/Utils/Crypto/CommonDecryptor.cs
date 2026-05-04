namespace Ruri.RipperHook.Crypto;

public record CommonDecryptor
{
    /// <summary>
    /// 标准解密（适用于 Mr0k, FairGuard 等）
    /// </summary>
    public virtual Span<byte> Decrypt(Span<byte> dataSpan) => dataSpan;

    /// <summary>
    /// 带索引的块解密（适用于 UnityChina 等需要 BlockIndex 的算法）
    /// </summary>
    public virtual Span<byte> Decrypt(Span<byte> dataSpan, int index) => Decrypt(dataSpan);

    /// <summary>
    /// 兼容性方法，部分旧代码可能通过 Block 调用
    /// </summary>
    public virtual void DecryptBlock(Span<byte> bytes, int size, int index)
    {
        // 默认实现调用带索引的 Decrypt
        Decrypt(bytes.Slice(0, size), index);
    }
}