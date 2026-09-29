using System.Threading.Channels;
using MiCamera.Net.Abstractions.Common.Models;

namespace MiCamera.Net.Server.Providers;

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

                        this.ResetForKeyFrameCore();
                        return;
                    }
                }

                if (!this.TryWrite(chunk))
                {
                    this.ResetForKeyFrameCore();
                    return;
                }

                this._waitingForKeyFrame = false;
                return;
            }

            if (!this.TryWrite(chunk))
            {
                this.ResetForKeyFrameCore();
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
