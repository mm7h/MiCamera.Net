namespace MiCamera.Net.RTSP.Abstractions.Web;

public sealed record CameraStreamInfoResponse(
    string StreamId,
    string CameraId,
    int Channel,
    string SourceCodec,
    string State,
    DateTimeOffset? LastReceivedAt,
    string RtspUrl,
    bool SnapshotAvailable,
    bool WebRtcAvailable);
