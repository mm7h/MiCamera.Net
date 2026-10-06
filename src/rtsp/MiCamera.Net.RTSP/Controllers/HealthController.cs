using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Abstractions.Streams;
using MiCamera.Net.Media.Runtime;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using MiCamera.Net.RTSP.Abstractions.Web;
using MiCamera.Net.RTSP.Server;
using Microsoft.AspNetCore.Mvc;

namespace MiCamera.Net.RTSP.Controllers;

/// <summary>Uses the same global Bearer authorization filter as every other controller.</summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController(ICameraStreamProvider source, IVideoSnapshotProvider snapshots,
    IMediaCapabilityProvider media, MiCameraServerOptions server, MiCameraRtspOptions options, RtspServerHostedService rtsp) : ControllerBase
{
    [HttpGet("live")]
    public IActionResult Live() => this.Ok(new { live = true });

    [HttpGet("ready")]
    public IActionResult Ready()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StreamHealthResponse[] streams = [.. source.Streams.Select(stream =>
        {
            source.TryGetSnapshot(stream.StreamId, out var state);
            bool receiving = state?.State == CameraStreamState.Streaming && state.LastReceivedAt is { } received &&
                now - received <= server.Streaming.IdleTimeout;
            bool snapshot = snapshots.TryGetSnapshot(stream.StreamId, out var frame) && frame is not null &&
                now - frame.CapturedAt <= server.Streaming.FirstKeyFrameTimeout;
            return new StreamHealthResponse(stream.StreamId, state?.State.ToString() ?? "Unknown", state?.LastReceivedAt,
                receiving, snapshot, options.WebRtc.Enabled && media.CanProvide(stream.StreamId, VideoCodec.H264, out _));
        })];
        bool native = MediaRuntimeDiagnostics.Current.FullyAvailable;
        bool mediaReady = streams.Length > 0 && native && streams.All(stream => stream.Receiving &&
            (!options.Snapshot.Enabled || stream.SnapshotAvailable) && (!options.WebRtc.Enabled || stream.WebRtcAvailable));
        bool ready = mediaReady && rtsp.Configured && rtsp.Listening;
        return this.StatusCode(ready ? 200 : 503, new HealthResponse(ready, native, streams, mediaReady, rtsp.Configured, rtsp.Listening));
    }
}
