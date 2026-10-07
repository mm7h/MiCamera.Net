using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.Abstractions.Common.Models;

public sealed record CameraStreamDescriptor(
    string StreamId,
    string CameraId,
    int Channel,
    VideoCodec Codec);
