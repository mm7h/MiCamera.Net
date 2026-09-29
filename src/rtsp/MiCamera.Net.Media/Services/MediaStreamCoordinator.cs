using System.Runtime.CompilerServices;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Abstractions.Streams;
using MiCamera.Net.Media.Runtime;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MiCamera.Net.Media.Services;

/// <summary>
/// Maintains exactly one upstream subscription per camera and fans normalized units out to RTSP and WebRTC consumers.
/// </summary>
public sealed class MediaStreamCoordinator : BackgroundService, INormalizedVideoStreamProvider, IVideoSnapshotProvider, IMediaCapabilityProvider
{
    private readonly ICameraStreamProvider _source;
    private readonly MiCameraServerOptions _serverOptions;
    private readonly MiCameraRtspOptions _rtspOptions;
    private readonly ILogger<MediaStreamCoordinator> _logger;
    private readonly IReadOnlyDictionary<string, StreamPipeline> _pipelines;

    public MediaStreamCoordinator(
        ICameraStreamProvider source,
        MiCameraServerOptions serverOptions,
        MiCameraRtspOptions rtspOptions,
        ILogger<MediaStreamCoordinator> logger)
    {
        this._source = source;
        this._serverOptions = serverOptions;
        this._rtspOptions = rtspOptions;
        this._logger = logger;
        this._pipelines = serverOptions.Streams.ToDictionary(
            stream => stream.StreamId,
            stream => new StreamPipeline(stream, rtspOptions, logger),
            StringComparer.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<VideoAccessUnit> SubscribeAsync(string streamId, VideoCodec? requestedCodec = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        StreamPipeline pipeline = this.GetPipeline(streamId);
        await foreach (VideoAccessUnit unit in pipeline.SubscribeAsync(requestedCodec, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return unit;
        }
    }

    public bool TryGetCodecParameters(string streamId, out VideoCodecParameters? parameters)
    {
        return this.GetPipeline(streamId).TryGetCodecParameters(out parameters);
    }

    public bool TryGetSnapshot(string streamId, out VideoSnapshot? snapshot)
    {
        if (!this._pipelines.TryGetValue(streamId, out StreamPipeline? pipeline))
        {
            snapshot = null;
            return false;
        }

        return pipeline.TryGetSnapshot(out snapshot);
    }

    public bool CanProvide(string streamId, VideoCodec codec, out string? reason)
    {
        if (!this._pipelines.TryGetValue(streamId, out StreamPipeline? pipeline))
        {
            reason = "The camera stream was not found.";
            return false;
        }

        return pipeline.CanProvide(codec, out reason);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FFmpegRuntime.Configure(this._rtspOptions.Media);
        if (!FFmpegRuntime.IsAvailable)
        {
            this._logger.LogWarning(
                FFmpegRuntime.Failure,
                "FFmpeg native libraries are unavailable. RTSP passthrough remains available; snapshots and H.265 WebRTC transcoding are disabled.");
        }

        Task[] workers = this._pipelines.Values
            .Select(pipeline => pipeline.RunAsync(this._source, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (StreamPipeline pipeline in this._pipelines.Values)
        {
            pipeline.Stop();
        }

        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        foreach (StreamPipeline pipeline in this._pipelines.Values)
        {
            pipeline.Dispose();
        }

        base.Dispose();
    }

    private StreamPipeline GetPipeline(string streamId)
    {
        if (this._pipelines.TryGetValue(streamId, out StreamPipeline? pipeline))
        {
            return pipeline;
        }

        throw new KeyNotFoundException($"The camera stream '{streamId}' is not configured.");
    }
}
