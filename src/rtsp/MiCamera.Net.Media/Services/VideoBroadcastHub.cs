using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MiCamera.Net.RTSP.Abstractions.Media;

namespace MiCamera.Net.Media.Services;

internal sealed class VideoBroadcastHub
{
    private readonly object _sync = new();
    private readonly Dictionary<long, VideoBroadcastSubscriber> _subscribers = [];
    private readonly List<VideoAccessUnit> _parameterUnits = [];
    private long _nextId;

    public async IAsyncEnumerable<VideoAccessUnit> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        VideoBroadcastSubscriber subscriber = new();
        long id;

        lock (this._sync)
        {
            id = ++this._nextId;
            this._subscribers.Add(id, subscriber);
        }

        try
        {
            await foreach (VideoAccessUnit unit in subscriber.Channel.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return unit;
            }
        }
        finally
        {
            lock (this._sync)
            {
                this._subscribers.Remove(id);
            }

            subscriber.Channel.Writer.TryComplete();
        }
    }

    public void Publish(VideoAccessUnit unit)
    {
        lock (this._sync)
        {
            if (unit.ContainsCodecParameters)
            {
                this._parameterUnits.RemoveAll(existing => existing.Codec == unit.Codec);
                this._parameterUnits.Add(unit);
            }

            foreach (VideoBroadcastSubscriber subscriber in this._subscribers.Values)
            {
                subscriber.Publish(unit, this._parameterUnits);
            }
        }
    }

    public void Complete()
    {
        lock (this._sync)
        {
            foreach (VideoBroadcastSubscriber subscriber in this._subscribers.Values)
            {
                subscriber.Channel.Writer.TryComplete();
            }

            this._subscribers.Clear();
        }
    }
}
