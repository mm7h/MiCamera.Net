using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.Abstractions.Common.Models;

public sealed record CameraStreamSnapshot(
    string StreamId,
    CameraStreamState State,
    long Sequence,
    DateTimeOffset? LastReceivedAt,
    string? LastError);
