namespace MiCamera.Net.Abstractions.ConfigSettings;

/// <summary>
/// Complete configuration for a MiCamera server host.
/// </summary>
public sealed class MiCameraServerOptions
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ServerInitialization Initialization { get; } = new();

    public MilocoOptions Miloco { get; set; } = new();

    public StreamingOptions Streaming { get; set; } = new();

    public ReconnectOptions Reconnect { get; set; } = new();

    public List<CameraStreamOptions> Streams { get; set; } = [];
}
