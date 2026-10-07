using System.Threading.Channels;
using MiCamera.Net.Abstractions.Common.Models;

namespace MiCamera.Net.Server.Providers;

/// <summary>
/// Bounded hand-off between camera reception and one downstream consumer. The queue absorbs
/// bursts; overflow preserves the buffered frames and resumes at the next key frame, so a dropped
/// reference frame cannot corrupt subsequent predictions.
/// </summary>
internal sealed class CameraStreamSubscription
{
    private readonly object _lock = new();
    private bool _waitingForKeyFrame = true;

    public CameraStreamSubscription(int bufferCapacity)
    {
        this.Channel = System.Threading.Channels.Channel.CreateBounded<EncodedVideoChunk>(new BoundedChannelOptions(bufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public Channel<EncodedVideoChunk> Channel { get; }

    public void ResetForKeyFrame()
    {
        lock (this._lock)
        {
            this.ResetForKeyFrameCore();
        }
    }

    public void Publish(EncodedVideoChunk chunk, IReadOnlyList<EncodedVideoChunk> codecParameters)
    {
        lock (this._lock)
        {
            if (this._waitingForKeyFrame)
            {
                if (!chunk.IsKeyFrame)
                {
                    return;
                }

                if (!chunk.ContainsCodecParameters)
                {
                    foreach (EncodedVideoChunk codecParameterChunk in codecParameters)
                    {
                        if (codecParameterChunk.Sequence == chunk.Sequence || this.TryWrite(codecParameterChunk))
                        {
                            continue;
                        }

                        return;
                    }
                }

                if (!this.TryWrite(chunk))
                {
                    return;
                }

                this._waitingForKeyFrame = false;
                return;
            }

            // A full queue means the consumer fell behind, not that its backlog is unusable: the
            // buffered chunks are still in decode order. Drop this newest frame and let the queue
            // drain. Draining the buffer here would cost up to the whole buffer plus a wait for the
            // next key frame, which a viewer sees as a multi-second freeze instead of one missing
            // frame. Keeping the queue bounded keeps the added latency bounded as well.
            if (!this.TryWrite(chunk))
            {
                this._waitingForKeyFrame = true;
            }
        }
    }

    private bool TryWrite(EncodedVideoChunk chunk)
    {
        return this.Channel.Writer.TryWrite(chunk);
    }

    private void ResetForKeyFrameCore()
    {
        while (this.Channel.Reader.TryRead(out _))
        {
        }

        this._waitingForKeyFrame = true;
    }
}
