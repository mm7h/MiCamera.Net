using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.RTSP.Abstractions.Media;

public sealed record VideoSnapshot(
    string StreamId,
    ReadOnlyMemory<byte> Jpeg,
    DateTimeOffset CapturedAt,
    long SourceSequence,
    int Width,
    int Height,
    VideoCodec SourceCodec);
