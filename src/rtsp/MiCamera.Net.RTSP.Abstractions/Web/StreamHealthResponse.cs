namespace MiCamera.Net.RTSP.Abstractions.Web;

public sealed record StreamHealthResponse(string StreamId, string State, DateTimeOffset? LastReceivedAt,
    bool Receiving, bool SnapshotAvailable, bool WebRtcAvailable);
