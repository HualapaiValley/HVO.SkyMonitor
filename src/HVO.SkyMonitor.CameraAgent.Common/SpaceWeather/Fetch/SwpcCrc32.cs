namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

/// <summary>Incremental reflected IEEE CRC-32 for gzip headers and decoded bytes.</summary>
internal static class SwpcCrc32
{
    public const uint Initial = uint.MaxValue;
    // Initialized once; no code writes this private table after initialization.
    private static readonly uint[] Table = CreateTable();

    public static uint Append(uint state, byte value) => Table[(state ^ value) & 255] ^ (state >> 8);

    public static uint Append(uint state, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) state = Append(state, value);
        return state;
    }

    public static uint Compute(ReadOnlySpan<byte> bytes) => ~Append(Initial, bytes);

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
            table[index] = value;
        }
        return table;
    }
}
