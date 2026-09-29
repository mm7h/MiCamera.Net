namespace MiCamera.Net.RTSP.Abstractions.ConfigSettings;

public sealed class MediaProcessingOptions
{
    public string NativeLibraryPath { get; set; } = string.Empty;

    public string H264EncoderName { get; set; } = "libx264";

    public int H264Bitrate { get; set; } = 2_500_000;

    public string H264Preset { get; set; } = "veryfast";

    public TimeSpan KeyFrameInterval { get; set; } = TimeSpan.FromSeconds(2);
}
