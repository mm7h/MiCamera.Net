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

    public TimeSpan TranscoderIdleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxPeersPerStream { get; set; } = 4;
}
