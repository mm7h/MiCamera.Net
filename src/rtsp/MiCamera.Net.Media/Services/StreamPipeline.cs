using System.Runtime.CompilerServices;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.Common.Models;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Abstractions.Streams;
using MiCamera.Net.Media.Runtime;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using Microsoft.Extensions.Logging;

namespace MiCamera.Net.Media.Services;

internal sealed class StreamPipeline : IDisposable
{
    private readonly CameraStreamOptions _stream;
    private readonly MiCameraRtspOptions _options;
    private readonly ILogger _logger;
    private readonly VideoAccessUnitClock _clock;
    private readonly VideoBroadcastHub _sourceHub = new();
    private readonly VideoBroadcastHub _h264Hub = new();
    private readonly object _sync = new();
    private readonly Dictionary<int, ReadOnlyMemory<byte>> _parameterSets = [];
    private FFmpegFrameProcessor? _processor;
    private Exception? _processorFailure;
    private volatile bool _keyFrameRequested;
    private VideoSnapshot? _snapshot;
    private int _h264SubscriberCount;
    private bool _disposed;

    public StreamPipeline(CameraStreamOptions stream, MiCameraRtspOptions options, ILogger logger)
    {
        this._stream = stream;
        this._options = options;
        this._logger = logger;
        this._clock = new VideoAccessUnitClock(stream.StreamId, stream.Codec, stream.NominalFrameRate);
    }

    public async IAsyncEnumerable<VideoAccessUnit> SubscribeAsync(
        VideoCodec? requestedCodec,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        VideoCodec codec = requestedCodec ?? this._stream.Codec;
        if (codec == this._stream.Codec)
        {
            await foreach (VideoAccessUnit unit in this._sourceHub.SubscribeAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return unit;
            }

            yield break;
        }

        if (codec != VideoCodec.H264 || this._stream.Codec != VideoCodec.H265)
        {
            throw new NotSupportedException(
                $"Camera stream '{this._stream.StreamId}' cannot provide {codec} from {this._stream.Codec}.");
        }

        if (!FFmpegRuntime.IsAvailable)
        {
            throw new InvalidOperationException("H.265 to H.264 transcoding requires FFmpeg native libraries.");
        }

        Interlocked.Increment(ref this._h264SubscriberCount);
        // Every new viewer needs a random access point as soon as possible.
        this._keyFrameRequested = true;
        try
        {
            await foreach (VideoAccessUnit unit in this._h264Hub.SubscribeAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return unit;
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref this._h264SubscriberCount) == 0)
            {
                _ = this.DisableTranscoderAfterIdleAsync();
            }
        }
    }

    public bool TryGetCodecParameters(out VideoCodecParameters? parameters)
    {
        lock (this._sync)
        {
            if (this._parameterSets.Count == 0)
            {
                parameters = null;
                return false;
            }

            parameters = new VideoCodecParameters(
                this._stream.StreamId,
                this._stream.Codec,
                this._parameterSets.OrderBy(static pair => pair.Key).Select(static pair => pair.Value).ToArray());
            return true;
        }
    }

    public bool TryGetSnapshot(out VideoSnapshot? snapshot)
    {
        lock (this._sync)
        {
            snapshot = this._snapshot;
            return snapshot is not null;
        }
    }

    public bool CanProvide(VideoCodec codec, out string? reason)
    {
        if (codec == this._stream.Codec)
        {
            reason = null;
            return true;
        }

        if (codec == VideoCodec.H264 && this._stream.Codec == VideoCodec.H265 && FFmpegRuntime.Report.FullyAvailable && this._processorFailure is null)
        {
            reason = null;
            return true;
        }

        reason = codec == VideoCodec.H264 && this._stream.Codec == VideoCodec.H265
            ? "H.265 to H.264 WebRTC conversion requires working FFmpeg decoders and encoders."
            : $"The camera stream cannot provide {codec} from {this._stream.Codec}.";
        return false;
    }

    public async Task RunAsync(ICameraStreamProvider source, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (EncodedVideoChunk chunk in source.SubscribeAsync(this._stream.StreamId, stoppingToken)
                .ConfigureAwait(false))
            {
                VideoAccessUnit unit = this._clock.Create(chunk);
                this.CacheCodecParameters(unit);
                this._sourceHub.Publish(unit);

                if (!this._options.Snapshot.Enabled && Volatile.Read(ref this._h264SubscriberCount) == 0)
                {
                    continue;
                }

                try
                {
                    ProcessResult? result;
                    lock (this._sync)
                    {
                        FFmpegFrameProcessor? processor = this.GetOrCreateProcessor();
                        if (processor is not null && this._keyFrameRequested)
                        {
                            this._keyFrameRequested = false;
                            processor.RequestKeyFrame();
                        }

                        result = processor?.Process(unit, Volatile.Read(ref this._h264SubscriberCount) > 0);
                        if (result is not null) this._processorFailure = null;
                    }

                    if (result is null)
                    {
                        continue;
                    }

                    if (result.Snapshot is not null)
                    {
                        lock (this._sync)
                        {
                            this._snapshot = result.Snapshot;
                        }
                    }

                    foreach (VideoAccessUnit h264 in result.H264AccessUnits)
                    {
                        this._h264Hub.Publish(h264);
                    }
                }
                catch (Exception exception)
                {
                    this._logger.LogError(exception, "FFmpeg processing failed for camera stream {StreamId}.", this._stream.StreamId);
                    lock (this._sync)
                    {
                        this._processorFailure = exception;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            this._logger.LogError(exception, "Media stream worker stopped for camera stream {StreamId}.", this._stream.StreamId);
        }
        finally
        {
            this._sourceHub.Complete();
            this._h264Hub.Complete();
        }
    }

    public void Stop()
    {
        this._sourceHub.Complete();
        this._h264Hub.Complete();
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        this.Stop();
        lock (this._sync)
        {
            this._processor?.Dispose();
            this._processor = null;
        }
    }

    private void CacheCodecParameters(VideoAccessUnit unit)
    {
        if (!unit.ContainsCodecParameters)
        {
            return;
        }

        IReadOnlyList<ReadOnlyMemory<byte>> nals = AnnexBBitstream.SplitNalUnits(unit.AnnexB);
        lock (this._sync)
        {
            foreach (ReadOnlyMemory<byte> nal in nals)
            {
                if (AnnexBBitstream.IsCodecParameterNal(this._stream.Codec, nal.Span))
                {
                    this._parameterSets[AnnexBBitstream.GetNalType(this._stream.Codec, nal.Span)] = nal.ToArray();
                }
            }
        }
    }

    private FFmpegFrameProcessor? GetOrCreateProcessor()
    {
        lock (this._sync)
        {
            if (this._processor is not null)
            {
                return this._processor;
            }

            if (this._processorFailure is not null)
            {
                return null;
            }

            if (FFmpegFrameProcessor.TryCreate(
                this._stream.StreamId,
                this._stream.Codec,
                this._stream.NominalFrameRate,
                this._options.Media,
                this._options.Snapshot.JpegQuality,
                out FFmpegFrameProcessor? processor,
                out Exception? error))
            {
                this._processor = processor;
                return processor;
            }

            this._processorFailure = error;
            this._logger.LogError(error, "FFmpeg initialization failed for camera stream {StreamId}.", this._stream.StreamId);
            return null;
        }
    }

    private async Task DisableTranscoderAfterIdleAsync()
    {
        try
        {
            await Task.Delay(this._options.WebRtc.TranscoderIdleTimeout).ConfigureAwait(false);
            if (Volatile.Read(ref this._h264SubscriberCount) == 0)
            {
                lock (this._sync)
                {
                    this._processor?.StopH264Transcoding();
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
