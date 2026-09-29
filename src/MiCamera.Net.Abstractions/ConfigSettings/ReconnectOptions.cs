namespace MiCamera.Net.Abstractions.ConfigSettings;

/// <summary>
/// Application-managed reconnection policy. Websocket.Client's built-in reconnect is disabled.
/// </summary>
public sealed class ReconnectOptions
{
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumDelay { get; set; } = TimeSpan.FromSeconds(30);

    public double BackoffMultiplier { get; set; } = 2d;

    /// <summary>
    /// A fraction between 0 and 1. For example, .2 means plus or minus twenty percent.
    /// </summary>
    public double JitterRatio { get; set; } = .2d;
}
