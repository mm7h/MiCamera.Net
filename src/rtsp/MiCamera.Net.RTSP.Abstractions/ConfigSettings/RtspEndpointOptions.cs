namespace MiCamera.Net.RTSP.Abstractions.ConfigSettings;

public sealed class RtspEndpointOptions
{
    public string ListenAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8554;

    public string PathPrefix { get; set; } = "/live";

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int RtpMtu { get; set; } = 1200;

    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
