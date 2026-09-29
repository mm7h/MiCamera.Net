using System.Collections.Concurrent;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.Common.Models;

namespace MiCamera.Net.Server.Providers;

internal sealed class CameraStreamHubState
{
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<long, CameraStreamSubscription> _subscriptions = new();
    private readonly int _subscriberBufferCapacity;
    private readonly List<EncodedVideoChunk> _latestCodecParameters = [];
    private long _nextSubscriptionId;
    private CameraStreamState _state = CameraStreamState.Stopped;
    private long _sequence;
    private DateTimeOffset? _lastReceivedAt;
    private string? _lastError;

    public CameraStreamHubState(CameraStreamDescriptor descriptor, int subscriberBufferCapacity)
    {
        this.Descriptor = descriptor;
        this._subscriberBufferCapacity = subscriberBufferCapacity;
    }

    public CameraStreamDescriptor Descriptor { get; }

    public CameraStreamSubscription AddSubscription()
    {
        long id = Interlocked.Increment(ref this._nextSubscriptionId);
        CameraStreamSubscription subscription = new(this._subscriberBufferCapacity);
        this._subscriptions[id] = subscription;
        return subscription;
    }

    public void RemoveSubscription(CameraStreamSubscription subscription)
    {
        foreach (KeyValuePair<long, CameraStreamSubscription> pair in this._subscriptions)
        {
            if (!ReferenceEquals(pair.Value, subscription))
            {
                continue;
            }

            this._subscriptions.TryRemove(pair.Key, out _);
            subscription.Channel.Writer.TryComplete();
            return;
        }
    }

    public void SetState(CameraStreamState state, string? error)
    {
        lock (this._stateLock)
        {
            this._state = state;
            this._lastError = error;
        }
    }

    public CameraStreamSnapshot CreateSnapshot()
    {
        lock (this._stateLock)
        {
            return new CameraStreamSnapshot(
                this.Descriptor.StreamId,
                this._state,
                this._sequence,
                this._lastReceivedAt,
                this._lastError);
        }
    }

    public void ResetSynchronization()
    {
        lock (this._stateLock)
        {
            this._latestCodecParameters.Clear();
        }

        this.ResetSubscribersForKeyFrame();
    }

    public void ResetSubscribersForKeyFrame()
    {
        foreach (CameraStreamSubscription subscription in this._subscriptions.Values)
        {
            subscription.ResetForKeyFrame();
        }
    }

    public void Publish(EncodedVideoChunk chunk)
    {
        IReadOnlyList<EncodedVideoChunk> codecParameters;

        lock (this._stateLock)
        {
            this._sequence = chunk.Sequence;
            this._lastReceivedAt = chunk.ReceivedAt;
            this._lastError = null;
            this._state = CameraStreamState.Streaming;

            if (chunk.ContainsCodecParameters)
            {
                this._latestCodecParameters.Add(chunk);
                if (this._latestCodecParameters.Count > 3)
                {
                    this._latestCodecParameters.RemoveAt(0);
                }
            }

            codecParameters = this._latestCodecParameters.ToArray();
        }

        foreach (CameraStreamSubscription subscription in this._subscriptions.Values)
        {
            subscription.Publish(chunk, codecParameters);
        }
    }

    public long NextSequence()
    {
        lock (this._stateLock)
        {
            return ++this._sequence;
        }
    }

    public void Stop()
    {
        this.SetState(CameraStreamState.Stopped, null);

        foreach (CameraStreamSubscription subscription in this._subscriptions.Values)
        {
            subscription.Channel.Writer.TryComplete();
        }

        this._subscriptions.Clear();
    }
}
