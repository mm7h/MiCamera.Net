using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.RTSP.Abstractions.Media;

public sealed record VideoCodecParameters(
    string StreamId,
    VideoCodec Codec,
    IReadOnlyList<ReadOnlyMemory<byte>> ParameterSets);
