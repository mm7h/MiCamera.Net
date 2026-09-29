namespace MiCamera.Net.RTSP.Abstractions.ConfigSettings;

public sealed class SnapshotOptions
{
    public bool Enabled { get; set; } = true;

    public int JpegQuality { get; set; } = 85;
}
