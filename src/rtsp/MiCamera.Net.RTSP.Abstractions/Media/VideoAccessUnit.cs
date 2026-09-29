using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.RTSP.Abstractions.Media;

/// <summary>
/// An immutable, complete Annex-B video access unit with a stable RTP clock.
/// </summary>
public sealed record VideoAccessUnit(
    string StreamId,
    VideoCodec Codec,
    ReadOnlyMemory<byte> AnnexB,
    long SourceSequence,
    uint Timestamp90Khz,
    uint Duration90Khz,
    bool IsKeyFrame,
    bool ContainsCodecParameters);
