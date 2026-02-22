namespace LibSparseSharp;

public static class Crc32
{
    private static readonly uint[] CrcTable = new uint[256];
    private const uint Polynomial = 0xEDB88320;

    static Crc32()
    {
        InitializeCrcTable();
    }

    /// <summary>
    /// 初始化 CRC32 查找表
    /// </summary>
    private static void InitializeCrcTable()
    {
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                {
                    crc = (crc >> 1) ^ Polynomial;
                }
                else
                {
                    crc >>= 1;
                }
            }
            CrcTable[i] = crc;
        }
    }

    /// <summary>
    /// 计算数据的 CRC32 校验和
    /// </summary>
    public static uint Calculate(byte[] data, int offset = 0, int length = -1)
    {
        if (length == -1)
        {
            length = data.Length - offset;
        }

        var crc = 0xFFFFFFFF;

        for (var i = offset; i < offset + length; i++)
        {
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// 计算数据的 CRC32 校验和（增量计算）
    /// </summary>
    public static uint Update(uint crc, byte[] data, int offset = 0, int length = -1)
    {
        if (length == -1)
        {
            length = data.Length - offset;
        }

        for (var i = offset; i < offset + length; i++)
        {
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>
    /// 开始新的 CRC32 计算
    /// </summary>
    public static uint Begin() => 0xFFFFFFFF;

    /// <summary>
    /// 完成 CRC32 计算
    /// </summary>
    public static uint Finish(uint crc) => crc ^ 0xFFFFFFFF;
}
