namespace Ruri.RipperHook.Crypto;

public class EndField_0_5_27_LZ4Inv : LZ4
{
    public new static EndField_0_5_27_LZ4Inv Instance { get; } = new();

    /// <summary>
    /// 0.5 版本使用了独特的位运算混淆 Token
    /// </summary>
    protected override (int encCount, int litCount) GetLiteralToken(ReadOnlySpan<byte> cmp, ref int cmpPos)
    {
        var val = cmp[cmpPos++];
        var lit = val & 0b00110011;
        var enc = val & 0b11001100;
        enc >>= 2;

        return ((enc & 0b11) | enc >> 2, (lit & 0b11) | lit >> 2);
    }

    protected override int GetChunkEnd(ReadOnlySpan<byte> cmp, ref int cmpPos) => cmp[cmpPos++] << 8 | cmp[cmpPos++] << 0;
}