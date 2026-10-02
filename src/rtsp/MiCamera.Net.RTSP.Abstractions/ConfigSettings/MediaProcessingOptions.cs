namespace MiCamera.Net.RTSP.Abstractions.ConfigSettings;

public sealed class MediaProcessingOptions
{
    public string NativeLibraryPath { get; set; } = string.Empty;

    public string H264EncoderName { get; set; } = "libx264";

    public int H264Bitrate { get; set; } = 2_500_000;

    public string H264Preset { get; set; } = "veryfast";

    /// <summary>
    /// Upper bound for the H.264 frames that browsers receive. Zero keeps the camera resolution,
    /// which only stays real time on a host that can also encode the source resolution.
    /// </summary>
    public int H264MaxWidth { get; set; }

    public int H264MaxHeight { get; set; }

    public TimeSpan KeyFrameInterval { get; set; } = TimeSpan.FromSeconds(2);
}
