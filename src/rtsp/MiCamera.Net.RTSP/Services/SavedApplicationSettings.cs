using MiCamera.Net.Abstractions.ConfigSettings;

namespace MiCamera.Net.RTSP.Services;

public sealed record SavedApplicationSettings(long Version, string MilocoBaseUrl, string MilocoPasswordMd5,
    string RtspUsername, string RtspDigestHa1, List<CameraStreamOptions> Streams);
