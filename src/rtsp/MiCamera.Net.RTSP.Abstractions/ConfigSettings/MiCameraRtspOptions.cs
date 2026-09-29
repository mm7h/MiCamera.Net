namespace MiCamera.Net.RTSP.Abstractions.ConfigSettings;

/// <summary>
/// Configuration for the optional RTSP, snapshot, and WebRTC services.
/// </summary>
public sealed class MiCameraRtspOptions
{
    public RtspEndpointOptions Rtsp { get; set; } = new();

    public HttpEndpointOptions Http { get; set; } = new();

    public WebRtcOptions WebRtc { get; set; } = new();

    public MediaProcessingOptions Media { get; set; } = new();

    public SnapshotOptions Snapshot { get; set; } = new();
}
