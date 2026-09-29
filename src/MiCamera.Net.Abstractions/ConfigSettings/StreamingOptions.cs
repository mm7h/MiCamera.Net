namespace MiCamera.Net.Abstractions.ConfigSettings;

/// <summary>
/// Limits and watchdogs applied to an individual video stream.
/// </summary>
public sealed class StreamingOptions
{
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan FirstKeyFrameTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public int MaxMessageBytes { get; set; } = 4 * 1024 * 1024;

    public int SubscriberBufferCapacity { get; set; } = 32;
}
