using System.IO;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// IEEE 802.3 표준 CRC32 해시 계산 유틸리티 (ZIP Central Directory CRC와 동일)
/// </summary>
public static class Crc32Helper
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }
        return crc ^ 0xFFFFFFFFu;
    }

    public static uint ComputeFile(string filePath)
    {
        uint crc = 0xFFFFFFFFu;
        byte[] buffer = new byte[65536];
        using var fs = File.OpenRead(filePath);
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                crc = (crc >> 8) ^ Table[(crc ^ buffer[i]) & 0xFF];
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
