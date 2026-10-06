using MiCamera.Net.Abstractions.ConfigSettings;

namespace MiCamera.Net.RTSP.Services;

public sealed record SaveSettingsRequest(long Version, string BaseUrl, string? Pin, string RtspUsername,
    string? RtspPassword, List<CameraStreamOptions> Streams);
