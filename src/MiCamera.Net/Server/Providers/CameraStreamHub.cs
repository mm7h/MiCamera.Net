using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.Common.Models;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Abstractions.Streams;

namespace MiCamera.Net.Server.Providers;

/// <summary>
/// Fans out each live Miloco stream without allowing a slow consumer to block camera reception.
/// </summary>
internal sealed class CameraStreamHub : ICameraStreamProvider
{
    private readonly MiCameraServerOptions _options;
    private static readonly IReadOnlyDictionary<string, CameraStreamHubState> EmptyStates = new Dictionary<string, CameraStreamHubState>();
    private volatile Lazy<IReadOnlyDictionary<string, CameraStreamHubState>> _initializedStates;
    private IReadOnlyDictionary<string, CameraStreamHubState> _states => this._options.Initialization.Configured
        ? this._initializedStates.Value : EmptyStates;

    public CameraStreamHub(MiCameraServerOptions options)
    {
        this._options = options;
        this._initializedStates = this.CreateStates();
    }

    public void Reset() => this._initializedStates = this.CreateStates();

    private Lazy<IReadOnlyDictionary<string, CameraStreamHubState>> CreateStates() => new(() => this._options.Streams.ToDictionary(
            stream => stream.StreamId,
            stream => new CameraStreamHubState(
                new CameraStreamDescriptor(stream.StreamId, stream.CameraDeviceId, stream.Channel, stream.Codec),
                this._options.Streaming.SubscriberBufferCapacity),
            StringComparer.OrdinalIgnoreCase));

    public IReadOnlyCollection<CameraStreamDescriptor> Streams =>
        this._states.Values.Select(static state => state.Descriptor).ToArray();

    public bool TryGetSnapshot(string streamId, out CameraStreamSnapshot? snapshot)
    {
        if (this._states.TryGetValue(streamId, out CameraStreamHubState? state))
        {
            snapshot = state.CreateSnapshot();
            return true;
        }

        snapshot = null;
        return false;
    }

    public async IAsyncEnumerable<EncodedVideoChunk> SubscribeAsync(
        string streamId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CameraStreamHubState state = this.GetState(streamId);
        CameraStreamSubscription subscription = state.AddSubscription();

        try
        {
            while (await subscription.Channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (subscription.Channel.Reader.TryRead(out EncodedVideoChunk? chunk))
                {
                    yield return chunk;
                }
            }
        }
        finally
        {
            state.RemoveSubscription(subscription);
        }
    }

    public void SetState(string streamId, CameraStreamState state, string? error = null)
    {
        this.GetState(streamId).SetState(state, error);
    }

    public void ResetSynchronization(string streamId)
    {
        this.GetState(streamId).ResetSynchronization();
    }

    public void ResetSubscribersForKeyFrame(string streamId)
    {
        this.GetState(streamId).ResetSubscribersForKeyFrame();
    }

    public void Publish(EncodedVideoChunk chunk)
    {
        this.GetState(chunk.StreamId).Publish(chunk);
    }

    public long NextSequence(string streamId)
    {
        return this.GetState(streamId).NextSequence();
    }

    public void StopAll()
    {
        foreach (CameraStreamHubState state in this._states.Values)
        {
            state.Stop();
        }
    }

    private CameraStreamHubState GetState(string streamId)
    {
        if (!this._states.TryGetValue(streamId, out CameraStreamHubState? state))
        {
            throw new KeyNotFoundException($"The camera stream '{streamId}' is not configured.");
        }

        return state;
    }

}
