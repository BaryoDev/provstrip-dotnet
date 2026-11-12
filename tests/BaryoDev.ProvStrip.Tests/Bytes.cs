namespace BaryoDev.ProvStrip.Tests;

/// <summary>Finds JPEG segments by scanning markers, so these tests do not need the JPEG container.</summary>
internal static class Bytes
{
    public static byte[] JpegPayload(byte[] jpeg, byte marker, string? startsWith = null)
    {
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF && jpeg[i + 1] != 0xDA)
        {
            var len = (jpeg[i + 2] << 8) | jpeg[i + 3];
            var payload = jpeg[(i + 4)..(i + 2 + len)];
            if (jpeg[i + 1] == marker && (startsWith is null || System.Text.Encoding.Latin1.GetString(payload).StartsWith(startsWith)))
                return payload;
            i += 2 + len;
        }
        throw new InvalidOperationException($"no segment 0xFF{marker:X2}");
    }

    public static byte[] PngChunk(string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var crc = PngContainer.Crc32([.. typeBytes, .. data]);
        return [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length,
            .. typeBytes, .. data, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    /// <summary>clean.png with one more chunk before IEND.</summary>
    public static byte[] CleanPngWith(string type, byte[] data)
    {
        var clean = Fixtures.Read("clean.png");
        return [.. clean[..^12], .. PngChunk(type, data), .. clean[^12..]];
    }
}
