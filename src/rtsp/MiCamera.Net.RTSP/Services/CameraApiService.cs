using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.Streams;
using MiCamera.Net.Media.Runtime;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using MiCamera.Net.RTSP.Abstractions.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MiCamera.Net.RTSP.Services;

public sealed class CameraApiService(
    ICameraStreamProvider streams,
    IVideoSnapshotProvider snapshots,
    IMediaCapabilityProvider mediaCapabilities,
    MiCameraRtspOptions options)
{
    private readonly ICameraStreamProvider _streams = streams;
    private readonly IVideoSnapshotProvider _snapshots = snapshots;
    private readonly IMediaCapabilityProvider _mediaCapabilities = mediaCapabilities;
    private readonly MiCameraRtspOptions _options = options;

    public IActionResult GetCameras()
    {
        List<CameraStreamInfoResponse> cameras = [.. this._streams.Streams.Select(stream =>
        {
            this._streams.TryGetSnapshot(stream.StreamId, out var state);
            bool hasSnapshot = this._snapshots.TryGetSnapshot(stream.StreamId, out _);
            return new CameraStreamInfoResponse(
                stream.StreamId,
                stream.CameraId,
                stream.Channel,
                stream.Codec.ToString(),
                state?.State.ToString() ?? "Unknown",
                state?.LastReceivedAt,
                this.CreateRtspUrl(stream.StreamId),
                hasSnapshot,
                this._options.WebRtc.Enabled && this._mediaCapabilities.CanProvide(stream.StreamId, MiCamera.Net.Abstractions.Common.Enums.VideoCodec.H264, out _));
        })];

        return new OkObjectResult(cameras);
    }

    public IActionResult GetSnapshot(string streamId)
    {
        var stream = this._streams.Streams.FirstOrDefault(stream => string.Equals(stream.StreamId, streamId, StringComparison.OrdinalIgnoreCase));
        if (stream is null)
        {
            return new NotFoundObjectResult(new { error = "未找到摄像头流。" });
        }

        MediaRuntimeReport runtime = MediaRuntimeDiagnostics.Current;
        VideoCodec codec = stream.Codec;
        if (!this._options.Snapshot.Enabled || !runtime.NativeLibrariesAvailable || !runtime.MjpegEncoder ||
            (codec == VideoCodec.H264 ? !runtime.H264Decoder : !runtime.HevcDecoder))
        {
            return new ObjectResult(new { error = "JPEG 快照已禁用，或所需的 FFmpeg 原生解码器或 MJPEG 编码器不可用。" })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }

        if (!this._snapshots.TryGetSnapshot(streamId, out VideoSnapshot? snapshot))
        {
            return new ObjectResult(new { error = "尚未获取到解码后的关键帧快照。" })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }

        return new FileContentResult(snapshot!.Jpeg.ToArray(), "image/jpeg")
        {
            LastModified = snapshot.CapturedAt
        };
    }

    private string CreateRtspUrl(string streamId)
    {
        string prefix = this._options.Rtsp.PathPrefix.TrimEnd('/');
        return $"rtsp://{this._options.Rtsp.ListenAddress}:{this._options.Rtsp.Port}{prefix}/{Uri.EscapeDataString(streamId)}";
    }
}
