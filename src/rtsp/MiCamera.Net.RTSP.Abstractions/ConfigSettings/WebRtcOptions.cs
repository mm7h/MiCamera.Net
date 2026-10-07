namespace MiCamera.Net.RTSP.Abstractions.ConfigSettings;

public sealed class WebRtcOptions
{
    public string? BindAddress { get; set; }

    public int? PortRangeStart { get; set; }

    public int? PortRangeEnd { get; set; }

    public bool Enabled { get; set; } = true;

    public List<IceServerOptions> IceServers { get; set; } = [];

    public TimeSpan IceGatheringTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan PendingSessionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan DisconnectedGracePeriod { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long each frame is reserved in front of the sender's clock. The camera relay delivers
    /// frames in bursts separated by holes, and this is the margin that absorbs them, so raising it
    /// trades latency for continuity. On camera streams measured here the holes reach about half a
    /// second, which is what the default covers. The sender also raises it on its own while a stall
    /// lasts longer than this value, and hands the surplus back afterwards.
    /// </summary>
    public TimeSpan PlayoutDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan TranscoderIdleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxPeersPerStream { get; set; } = 4;
}
