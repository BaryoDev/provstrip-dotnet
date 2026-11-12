namespace BaryoDev.ProvStrip.Formats;

/// <summary>A drop (Replacement null) or a replacement of one segment.</summary>
internal sealed record SegmentEdit(Segment Segment, byte[]? Replacement);

internal interface IContainer
{
    string Format { get; }

    bool Sniff(ReadOnlySpan<byte> bytes);

    /// <summary>Every segment, in file order. Does not classify.</summary>
    IReadOnlyList<Segment> ReadSegments(ReadOnlyMemory<byte> bytes);

    /// <summary>Required to decode the image, or otherwise never a finding.</summary>
    bool IsStructural(Segment segment, ReadOnlySpan<byte> bytes);

    /// <summary>Whether an unknown segment can be removed without breaking the file.</summary>
    bool IsSafeToDrop(Segment segment);

    /// <summary>The bytes the rules look at.</summary>
    ReadOnlyMemory<byte> Payload(Segment segment, ReadOnlyMemory<byte> bytes);

    /// <summary>Copies the file, applying the edits. Never re-encodes what it keeps.</summary>
    byte[] Rewrite(ReadOnlyMemory<byte> bytes, IReadOnlyList<SegmentEdit> edits);
}
