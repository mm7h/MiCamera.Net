namespace MiCamera.Net.Sample.Server.Configuration;

internal sealed class FFmpegConfiguration
{
    public string Path { get; set; } = string.Empty;
    public string H264EncoderName { get; set; } = "libx264";
    public int H264Bitrate { get; set; } = 2_500_000;
    public string H264Preset { get; set; } = "veryfast";
    public int H264MaxWidth { get; set; }
    public int H264MaxHeight { get; set; }
    public TimeSpan KeyFrameInterval { get; set; } = TimeSpan.FromSeconds(2);
}
