using System.Text.Json;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;

namespace MiCamera.Net.Sample.Server.Configuration;

internal sealed class MediaServerConfiguration
{
    public string ListenAddress { get; set; } = "http://127.0.0.1:5080";
    public string BearerToken { get; set; } = string.Empty;
    public List<string> AllowedOrigins { get; set; } = [];
    public FFmpegConfiguration FFmpeg { get; set; } = new();
    public RtspEndpointOptions Rtsp { get; set; } = new();
    public WebRtcOptions WebRtc { get; set; } = new();
    public SnapshotOptions Snapshot { get; set; } = new();

    public MiCameraRtspOptions ToRuntimeOptions()
    {
        if (this.FFmpeg is null)
        {
            throw new JsonException("MediaServer.FFmpeg 必须是 JSON 对象。");
        }

        return new MiCameraRtspOptions
        {
            Http = new HttpEndpointOptions
            {
                ListenUrl = this.ListenAddress,
                BearerToken = this.BearerToken,
                AllowedOrigins = this.AllowedOrigins
            },
            Media = new MediaProcessingOptions
            {
                FFmpegLibPath = string.IsNullOrWhiteSpace(this.FFmpeg.Path) || System.IO.Path.IsPathFullyQualified(this.FFmpeg.Path)
                    ? this.FFmpeg.Path
                    : System.IO.Path.GetFullPath(this.FFmpeg.Path, AppContext.BaseDirectory),
                H264EncoderName = this.FFmpeg.H264EncoderName,
                H264Bitrate = this.FFmpeg.H264Bitrate,
                H264Preset = this.FFmpeg.H264Preset,
                H264MaxWidth = this.FFmpeg.H264MaxWidth,
                H264MaxHeight = this.FFmpeg.H264MaxHeight,
                KeyFrameInterval = this.FFmpeg.KeyFrameInterval
            },
            Rtsp = this.Rtsp,
            WebRtc = this.WebRtc,
            Snapshot = this.Snapshot
        };
    }
}
