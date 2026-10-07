using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.Abstractions.Common.Models;

/// <summary>
/// An owned Annex-B encoded video message received from Miloco. A chunk may contain more than one NAL unit.
/// </summary>
public sealed record EncodedVideoChunk(
    string StreamId,
    VideoCodec Codec,
    ReadOnlyMemory<byte> Data,
    long Sequence,
    DateTimeOffset ReceivedAt,
    bool IsKeyFrame,
    bool ContainsCodecParameters);
