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
    private volatile Lazy<IReadOnlyDictionary<string, StreamPipeline>> _initializedPipelines;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationToken _hostToken;
    private CancellationTokenSource? _generation;
    private Task _workers = Task.CompletedTask;
    private bool _suspended;
    private static readonly IReadOnlyDictionary<string, StreamPipeline> s_emptyPipelines = new Dictionary<string, StreamPipeline>();
    private IReadOnlyDictionary<string, StreamPipeline> _pipelines => this._serverOptions.Initialization.Configured
        ? this._initializedPipelines.Value : s_emptyPipelines;

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
        this._initializedPipelines = this.CreatePipelines();
    }

    private Lazy<IReadOnlyDictionary<string, StreamPipeline>> CreatePipelines() => new(() => this._serverOptions.Streams.ToDictionary(
            stream => stream.StreamId,
            stream => new StreamPipeline(stream, this._rtspOptions, this._logger),
            StringComparer.OrdinalIgnoreCase));

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

    public void RequestKeyFrame(string streamId) => this.GetPipeline(streamId).RequestKeyFrame();

    public bool CanProvide(string streamId, VideoCodec codec, out string? reason)
    {
        if (!this._pipelines.TryGetValue(streamId, out StreamPipeline? pipeline))
        {
            reason = "未找到摄像头流。";
            return false;
        }

        return pipeline.CanProvide(codec, out reason);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this._hostToken = stoppingToken;
        FFmpegRuntime.Configure(this._rtspOptions.Media);
        if (!FFmpegRuntime.IsAvailable)
        {
            this._logger.LogWarning(
                FFmpegRuntime.Failure,
                "FFmpeg 原生库不可用。RTSP 透传仍可使用；快照和 H.265 WebRTC 转码已禁用。");
        }

        await this._serverOptions.Initialization.WaitAsync(stoppingToken).ConfigureAwait(false);
        await this._lifecycleLock.WaitAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            if (!this._suspended)
            {
                this.StartWorkers();
            }
        }
        finally { this._lifecycleLock.Release(); }
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        finally { await this.SuspendAsync().ConfigureAwait(false); }
    }

    public async Task SuspendAsync()
    {
        await this._lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            this._suspended = true;
            this._generation?.Cancel();
            foreach (StreamPipeline pipeline in this._pipelines.Values)
            {
                pipeline.Stop();
            }

            await this._workers.ConfigureAwait(false);
            this._generation?.Dispose();
            this._generation = null;
            foreach (StreamPipeline pipeline in this._pipelines.Values)
            {
                pipeline.Dispose();
            }
        }
        finally { this._lifecycleLock.Release(); }
    }

    public async Task ResumeAsync()
    {
        await this._lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            this._hostToken.ThrowIfCancellationRequested();
            this._initializedPipelines = this.CreatePipelines();
            this._suspended = false;
            this.StartWorkers();
        }
        finally { this._lifecycleLock.Release(); }
    }

    private void StartWorkers()
    {
        if (this._generation is not null)
        {
            return;
        }

        this._generation = CancellationTokenSource.CreateLinkedTokenSource(this._hostToken);
        CancellationToken token = this._generation.Token;
        this._workers = Task.WhenAll(this._pipelines.Values
            .Select(pipeline => Task.Run(() => pipeline.RunAsync(this._source, token))));
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

        throw new KeyNotFoundException($"摄像头流“{streamId}”尚未配置。");
    }
}
