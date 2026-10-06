using MiCamera.Net.Abstractions.ConfigSettings;

namespace MiCamera.Net.RTSP.Services;

public sealed record SettingsView(long Version, string MilocoBaseUrl, bool HasMilocoPin, string RtspUsername,
    bool HasRtspPassword, IReadOnlyList<CameraStreamOptions> Streams);
