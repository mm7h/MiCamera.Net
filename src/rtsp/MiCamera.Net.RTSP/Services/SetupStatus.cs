namespace MiCamera.Net.RTSP.Services;

public sealed record SetupStatus(bool Configured, bool Listening, string? Username, long Version, bool ApplyPending);
