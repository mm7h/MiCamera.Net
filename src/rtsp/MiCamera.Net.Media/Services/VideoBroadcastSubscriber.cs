using System.Threading.Channels;
using MiCamera.Net.RTSP.Abstractions.Media;

namespace MiCamera.Net.Media.Services;

internal sealed class VideoBroadcastSubscriber
{
    private bool _waitingForKeyFrame = true;

    public VideoBroadcastSubscriber()
    {
        this.Channel = System.Threading.Channels.Channel.CreateBounded<VideoAccessUnit>(
            new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
    }

    public Channel<VideoAccessUnit> Channel { get; }

    public void Publish(VideoAccessUnit unit, IReadOnlyList<VideoAccessUnit> parameterUnits)
    {
        if (this._waitingForKeyFrame)
        {
            if (!unit.IsKeyFrame)
            {
                return;
            }

            if (!unit.ContainsCodecParameters)
            {
                foreach (VideoAccessUnit parameters in parameterUnits.Where(parameters => parameters.Codec == unit.Codec))
                {
                    if (!this.Channel.Writer.TryWrite(parameters))
                    {
                        this.Reset();
                        return;
                    }
                }
            }

            if (!this.Channel.Writer.TryWrite(unit))
            {
                this.Reset();
                return;
            }

            this._waitingForKeyFrame = false;
            return;
        }

        if (!this.Channel.Writer.TryWrite(unit))
        {
            this.Reset();
        }
    }

    private void Reset()
    {
        while (this.Channel.Reader.TryRead(out _))
        {
        }

        this._waitingForKeyFrame = true;
    }
}
