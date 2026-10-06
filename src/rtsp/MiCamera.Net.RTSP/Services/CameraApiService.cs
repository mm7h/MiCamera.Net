using MiCamera.Net.Abstractions.Streams;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using MiCamera.Net.RTSP.Abstractions.Web;
using MiCamera.Net.Media.Runtime;
using MiCamera.Net.Abstractions.Common.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MiCamera.Net.RTSP.Services;

public sealed class CameraApiService
{
    private readonly ICameraStreamProvider _streams;
    private readonly IVideoSnapshotProvider _snapshots;
    private readonly IMediaCapabilityProvider _mediaCapabilities;
    private readonly MiCameraRtspOptions _options;

    public CameraApiService(
        ICameraStreamProvider streams,
        IVideoSnapshotProvider snapshots,
        IMediaCapabilityProvider mediaCapabilities,
        MiCameraRtspOptions options)
    {
        this._streams = streams;
        this._snapshots = snapshots;
        this._mediaCapabilities = mediaCapabilities;
        this._options = options;
    }

    public IActionResult GetCameras()
    {
        List<CameraStreamInfoResponse> cameras = this._streams.Streams.Select(stream =>
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
        }).ToList();

        return new OkObjectResult(cameras);
    }

    public IActionResult GetSnapshot(string streamId)
    {
        var stream = this._streams.Streams.FirstOrDefault(stream => string.Equals(stream.StreamId, streamId, StringComparison.OrdinalIgnoreCase));
        if (stream is null)
        {
            return new NotFoundObjectResult(new { error = "The camera stream was not found." });
        }

        MediaRuntimeReport runtime = MediaRuntimeDiagnostics.Current;
        VideoCodec codec = stream.Codec;
        if (!this._options.Snapshot.Enabled || !runtime.NativeLibrariesAvailable || !runtime.MjpegEncoder ||
            (codec == VideoCodec.H264 ? !runtime.H264Decoder : !runtime.HevcDecoder))
        {
            return new ObjectResult(new { error = "JPEG snapshots are disabled or required FFmpeg native decoder/MJPEG encoder capabilities are unavailable." })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }

        if (!this._snapshots.TryGetSnapshot(streamId, out VideoSnapshot? snapshot))
        {
            return new ObjectResult(new { error = "A decoded key-frame snapshot is not available yet." })
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
