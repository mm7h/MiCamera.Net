namespace MiCamera.Net.RTSP.Abstractions.ConfigSettings;

public sealed class HttpEndpointOptions
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:5080";

    public string BearerToken { get; set; } = string.Empty;

    public List<string> AllowedOrigins { get; set; } = [];
}
