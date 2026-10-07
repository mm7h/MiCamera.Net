namespace MiCamera.Net.RTSP.Abstractions.ConfigSettings;

public sealed class IceServerOptions
{
    public string Url { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Credential { get; set; } = string.Empty;
}
