using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.Common.Models;
using MiCamera.Net.RTSP.Abstractions.Media;

namespace MiCamera.Net.Media.Services;

/// <summary>
/// Converts the upstream encoded messages into owned media units and a deterministic RTP clock.
/// </summary>
internal sealed class VideoAccessUnitClock
{
    private readonly string _streamId;
    private readonly VideoCodec _codec;
    private readonly uint _frameDuration;
    private uint _nextTimestamp;

    public VideoAccessUnitClock(string streamId, VideoCodec codec, double nominalFrameRate)
    {
        this._streamId = streamId;
        this._codec = codec;
        this._frameDuration = (uint)Math.Max(1, Math.Round(90_000d / nominalFrameRate));
    }

    public VideoAccessUnit Create(EncodedVideoChunk chunk)
    {
        byte[] annexB = chunk.Data.ToArray();
        bool isKeyFrame = chunk.IsKeyFrame || AnnexBBitstream.IsKeyFrame(this._codec, annexB);
        bool hasParameters = chunk.ContainsCodecParameters || AnnexBBitstream.ContainsCodecParameters(this._codec, annexB);
        VideoAccessUnit unit = new(
            this._streamId,
            this._codec,
            annexB,
            chunk.Sequence,
            this._nextTimestamp,
            this._frameDuration,
            isKeyFrame,
            hasParameters);

        this._nextTimestamp += this._frameDuration;
        return unit;
    }
}
