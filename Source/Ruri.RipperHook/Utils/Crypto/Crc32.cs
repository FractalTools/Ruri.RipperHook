namespace Ruri.RipperHook.Crypto;

public static class Crc32
{
    private static readonly uint[] CrcTable = GenerateCrcTable();

    private static uint[] GenerateCrcTable()
    {
        uint[] table = new uint[256];
        const uint polynomial = 0xEDB88320;

        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ polynomial;
                else
                    crc >>= 1;
            }
            table[i] = crc;
        }

        return table;
    }

    public static int Calculate(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFF;

        for (int i = 0; i < length; i++)
        {
            byte b = data[offset + i];
            crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        }

        return (int)(~crc);
    }
}
