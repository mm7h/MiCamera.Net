namespace MiCamera.Net.RTSP.Abstractions.ConfigSettings;

public sealed class SnapshotOptions
{
    public bool Enabled { get; set; } = true;

    public int JpegQuality { get; set; } = 85;

    /// <summary>Minimum interval between full-resolution JPEG encodes; zero refreshes every key frame.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
}
