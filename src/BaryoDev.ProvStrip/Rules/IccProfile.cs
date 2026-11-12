using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BaryoDev.ProvStrip.Rules;

internal static class IccProfile
{
    /// <summary>Real profiles are kilobytes. A zlib stream that inflates past this is refused.</summary>
    internal const int MaxProfileBytes = 4 * 1024 * 1024;

    /// <summary>The profile's description (v2 desc or v4 mluc), or null when it cannot be read.</summary>
    public static string? Description(ReadOnlySpan<byte> profile)
    {
        if (profile.Length < 132) return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(profile[128..]);
        for (var i = 0; i < count; i++)
        {
            var entry = 132 + i * 12;
            if (entry + 12 > profile.Length) return null;
            if (!profile.Slice(entry, 4).SequenceEqual("desc"u8)) continue;
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(profile[(entry + 4)..]);
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(profile[(entry + 8)..]);
            if (offset < 0 || size < 12 || offset + size > profile.Length) return null;
            return ReadText(profile.Slice(offset, size));
        }
        return null;
    }

    /// <summary>The profile inside a segment payload, which each format wraps differently.</summary>
    public static byte[]? FromPayload(string format, ReadOnlySpan<byte> payload)
    {
        switch (format)
        {
            case "jpeg":
                // "ICC_PROFILE\0", sequence number, count
                return payload.Length > 14 ? payload[14..].ToArray() : null;
            case "png":
                // keyword, NUL, compression method, zlib stream
                var nul = payload.IndexOf((byte)0);
                if (nul < 0 || nul + 2 > payload.Length) return null;
                return Inflate(payload[(nul + 2)..]);
            default:
                return payload.ToArray();
        }
    }

    static byte[]? Inflate(ReadOnlySpan<byte> compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed.ToArray());
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxProfileBytes) return null;
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    static string? ReadText(ReadOnlySpan<byte> tag)
    {
        if (tag[..4].SequenceEqual("desc"u8))
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
            if (length <= 0 || 12 + length > tag.Length) return null;
            return Encoding.ASCII.GetString(tag.Slice(12, length)).TrimEnd('\0');
        }
        if (tag[..4].SequenceEqual("mluc"u8) && tag.Length >= 28)
        {
            var records = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
            if (records == 0) return null;
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[20..]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[24..]);
            if (offset < 0 || length < 0 || offset + length > tag.Length) return null;
            return Encoding.BigEndianUnicode.GetString(tag.Slice(offset, length)).TrimEnd('\0');
        }
        return null;
    }
}
