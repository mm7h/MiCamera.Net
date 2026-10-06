using System.Threading.Channels;
using MiCamera.Net.RTSP.Abstractions.Media;

namespace MiCamera.Net.Media.Services;

internal sealed class VideoBroadcastSubscriber
{
    private bool _waitingForKeyFrame = true;

    public VideoBroadcastSubscriber()
    {
        this.Channel = System.Threading.Channels.Channel.CreateBounded<VideoAccessUnit>(
            // At 20 fps, the maximum two-second playout delay needs 40 frames plus room for bursts.
            new BoundedChannelOptions(64)
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
                        return;
                    }
                }
            }

            if (!this.Channel.Writer.TryWrite(unit))
            {
                return;
            }

            this._waitingForKeyFrame = false;
            return;
        }

        if (!this.Channel.Writer.TryWrite(unit))
        {
            // Keep the decodable backlog. Once a reference is dropped, skip predictions until
            // the next key frame instead of clearing seconds of video from the queue.
            this._waitingForKeyFrame = true;
        }
    }
}
