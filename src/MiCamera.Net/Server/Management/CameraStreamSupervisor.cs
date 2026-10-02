using System.Net.WebSockets;
using System.Threading.Channels;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.Common.Models;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Server.Common;
using MiCamera.Net.Server.Protocol.Miloco;
using MiCamera.Net.Server.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Websocket.Client;

namespace MiCamera.Net.Server.Management;

/// <summary>
/// Runs an independent authenticated WebSocket stream loop for every configured camera channel.
/// </summary>
internal sealed class CameraStreamSupervisor : BackgroundService
{
    private readonly MiCameraServerOptions _options;
    private readonly MilocoSessionClient _session;
    private readonly MilocoWebSocketClientFactory _webSocketFactory;
    private readonly CameraStreamHub _streamHub;
    private readonly ILogger<CameraStreamSupervisor> _logger;

    public CameraStreamSupervisor(
        MiCameraServerOptions options,
        MilocoSessionClient session,
        MilocoWebSocketClientFactory webSocketFactory,
        CameraStreamHub streamHub,
        ILogger<CameraStreamSupervisor> logger)
    {
        this._options = options;
        this._session = session;
        this._webSocketFactory = webSocketFactory;
        this._streamHub = streamHub;
        this._logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task[] streamTasks = this._options.Streams
            .Select(stream => this.RunStreamAsync(stream, stoppingToken))
            .ToArray();

        return Task.WhenAll(streamTasks);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        this._streamHub.StopAll();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunStreamAsync(CameraStreamOptions stream, CancellationToken stoppingToken)
    {
        int failureCount = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                this._streamHub.SetState(stream.StreamId, CameraStreamState.Authenticating);
                await this._session.EnsureAuthenticatedAsync(stoppingToken).ConfigureAwait(false);

                this._streamHub.ResetSynchronization(stream.StreamId);
                this._streamHub.SetState(stream.StreamId, CameraStreamState.Connecting);
                await this.RunConnectedStreamAsync(stream, stoppingToken).ConfigureAwait(false);

                failureCount = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failureCount++;
                this._session.InvalidateAuthentication();

                string reason = CreateSafeErrorMessage(exception);
                this._streamHub.SetState(stream.StreamId, CameraStreamState.Reconnecting, reason);

                TimeSpan delay = this.GetReconnectDelay(failureCount);
                this._logger.LogWarning(
                    exception,
                    "Camera stream {StreamId} disconnected. Reconnecting in {ReconnectDelay}.",
                    stream.StreamId,
                    delay);

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        this._streamHub.SetState(stream.StreamId, CameraStreamState.Stopped);
    }

    private async Task RunConnectedStreamAsync(CameraStreamOptions stream, CancellationToken stoppingToken)
    {
        Uri streamUri = this._session.BuildVideoStreamUri(stream.CameraDeviceId, stream.Channel);
        using WebsocketClient client = this._webSocketFactory.Create(streamUri);
        using CancellationTokenSource connectionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        Channel<byte[]> ingress = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(
            Math.Max(4, this._options.Streaming.SubscriberBufferCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        TaskCompletionSource<Exception> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long lastMessageTicks = DateTime.UtcNow.Ticks;
        int droppedMessages = 0;
        int hasPublishedKeyFrame = 0;

        using IDisposable messageSubscription = client.MessageReceived.Subscribe(message =>
        {
            if (message.MessageType == WebSocketMessageType.Text)
            {
                completion.TrySetException(new InvalidOperationException(
                    "Miloco sent an unexpected text message; its contents have been suppressed."));
                return;
            }

            if (message.MessageType != WebSocketMessageType.Binary || message.Binary is not { Length: > 0 } data)
            {
                return;
            }

            if (data.Length > this._options.Streaming.MaxMessageBytes)
            {
                completion.TrySetException(new InvalidOperationException(
                    $"Miloco sent a {data.Length}-byte message, exceeding the configured limit."));
                return;
            }

            Interlocked.Exchange(ref lastMessageTicks, DateTime.UtcNow.Ticks);

            if (!ingress.Writer.TryWrite(data))
            {
                Interlocked.Exchange(ref droppedMessages, 1);
            }
        });

        using IDisposable disconnectionSubscription = client.DisconnectionHappened.Subscribe(info =>
        {
            info.CancelReconnection = true;
            Exception exception = info.Exception ?? new WebSocketException(
                $"Miloco WebSocket closed: {info.CloseStatus}");
            completion.TrySetException(exception);
        });

        Task? consumeTask = null;
        Task? watchdogTask = null;

        try
        {
            await client.StartOrFail().ConfigureAwait(false);
            this._streamHub.SetState(stream.StreamId, CameraStreamState.WaitingForKeyFrame);

            consumeTask = this.ConsumeIncomingChunksAsync(
                stream,
                ingress.Reader,
                () => Interlocked.Exchange(ref droppedMessages, 0) == 1,
                () => Interlocked.Exchange(ref hasPublishedKeyFrame, 1),
                connectionCancellation.Token);

            watchdogTask = this.WatchConnectionAsync(
                stream.StreamId,
                () => Volatile.Read(ref hasPublishedKeyFrame) == 1,
                () => new DateTime(Interlocked.Read(ref lastMessageTicks), DateTimeKind.Utc),
                connectionCancellation.Token);

            Task finishedTask = await Task.WhenAny(consumeTask, watchdogTask, completion.Task)
                .ConfigureAwait(false);

            if (ReferenceEquals(finishedTask, completion.Task))
            {
                throw await completion.Task.ConfigureAwait(false);
            }

            await finishedTask.ConfigureAwait(false);
            throw new InvalidOperationException("The Miloco stream ended unexpectedly.");
        }
        finally
        {
            connectionCancellation.Cancel();
            ingress.Writer.TryComplete();

            if (client.IsRunning)
            {
                try
                {
                    await client.StopOrFail(WebSocketCloseStatus.NormalClosure, "MiCamera stream reconnecting")
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    this._logger.LogDebug(exception, "The Miloco WebSocket for {StreamId} did not close cleanly.", stream.StreamId);
                }
            }

            await ObserveStoppedTaskAsync(consumeTask).ConfigureAwait(false);
            await ObserveStoppedTaskAsync(watchdogTask).ConfigureAwait(false);
        }
    }

    private async Task ConsumeIncomingChunksAsync(
        CameraStreamOptions stream,
        ChannelReader<byte[]> reader,
        Func<bool> consumeDropSignal,
        Action markKeyFramePublished,
        CancellationToken cancellationToken)
    {
        List<byte[]> cachedParameters = [];
        bool waitingForKeyFrame = true;

        await foreach (byte[] data in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (consumeDropSignal())
            {
                cachedParameters.Clear();
                waitingForKeyFrame = true;
                this._streamHub.ResetSubscribersForKeyFrame(stream.StreamId);
                this._streamHub.SetState(stream.StreamId, CameraStreamState.WaitingForKeyFrame);
                this._logger.LogWarning(
                    "Input processing for camera stream {StreamId} fell behind. Waiting for the next key frame.",
                    stream.StreamId);
            }

            BitstreamInspection inspection = AnnexBBitstreamInspector.Inspect(data, stream.Codec);

            if (waitingForKeyFrame)
            {
                if (inspection.ContainsCodecParameters)
                {
                    cachedParameters.Add(data);
                    if (cachedParameters.Count > 3)
                    {
                        cachedParameters.RemoveAt(0);
                    }
                }

                if (!inspection.IsKeyFrame)
                {
                    continue;
                }

                if (!inspection.ContainsCodecParameters)
                {
                    foreach (byte[] cachedParameterChunk in cachedParameters)
                    {
                        this.Publish(stream, cachedParameterChunk, false, true);
                    }
                }

                this.Publish(stream, data, true, inspection.ContainsCodecParameters);
                waitingForKeyFrame = false;
                markKeyFramePublished();
                continue;
            }

            this.Publish(
                stream,
                data,
                inspection.IsKeyFrame,
                inspection.ContainsCodecParameters);
        }
    }

    private async Task WatchConnectionAsync(
        string streamId,
        Func<bool> hasPublishedKeyFrame,
        Func<DateTime> lastMessageUtc,
        CancellationToken cancellationToken)
    {
        DateTime startedAt = DateTime.UtcNow;
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTime now = DateTime.UtcNow;

            if (!hasPublishedKeyFrame() && now - startedAt >= this._options.Streaming.FirstKeyFrameTimeout)
            {
                throw new TimeoutException($"Miloco stream '{streamId}' did not provide a key frame in time.");
            }

            if (now - lastMessageUtc() >= this._options.Streaming.IdleTimeout)
            {
                throw new TimeoutException($"Miloco stream '{streamId}' did not provide data in time.");
            }
        }
    }

    private void Publish(
        CameraStreamOptions stream,
        byte[] data,
        bool isKeyFrame,
        bool containsCodecParameters)
    {
        this._streamHub.Publish(new EncodedVideoChunk(
            stream.StreamId,
            stream.Codec,
            data,
            this._streamHub.NextSequence(stream.StreamId),
            DateTimeOffset.UtcNow,
            isKeyFrame,
            containsCodecParameters));
    }

    private TimeSpan GetReconnectDelay(int failureCount)
    {
        double exponent = Math.Max(0, failureCount - 1);
        double milliseconds = this._options.Reconnect.InitialDelay.TotalMilliseconds *
            Math.Pow(this._options.Reconnect.BackoffMultiplier, exponent);
        milliseconds = Math.Min(milliseconds, this._options.Reconnect.MaximumDelay.TotalMilliseconds);

        double jitterRange = this._options.Reconnect.JitterRatio;
        double jitter = 1d + ((Random.Shared.NextDouble() * 2d - 1d) * jitterRange);
        return TimeSpan.FromMilliseconds(Math.Max(1d, milliseconds * jitter));
    }

    private static string CreateSafeErrorMessage(Exception exception)
    {
        string message = exception.Message;
        return message.Length <= 512 ? message : string.Concat(message.AsSpan(0, 512), "…");
    }

    private static async Task ObserveStoppedTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }
}
