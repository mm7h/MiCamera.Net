using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.Common.Models;
using MiCamera.Net.RTSP.Abstractions.Media;

namespace MiCamera.Net.Media.Services;

/// <summary>
/// Converts upstream encoded messages into owned media units using their receive-time RTP clock.
/// The clock advances by the average arrival period over a short window rather than by each
/// individual arrival delta. The relay hands frames over in bursts separated by holes, so a
/// per-frame delta collapses to almost nothing inside a burst and jumps at a hole; a receiver
/// renders on this clock and discards a burst compressed into a single instant as late. Averaging
/// keeps the clock on the source's real cadence, and because the window only ever holds real
/// elapsed time the timeline cannot drift away from the source.
/// </summary>
internal sealed class VideoAccessUnitClock(string streamId, VideoCodec codec, double nominalFrameRate)
{
    /// <summary>
    /// Arrival samples behind the cadence estimate: about two seconds at 20 fps. Long enough to
    /// average out relay bursts, short enough to follow a genuine change of source frame rate.
    /// </summary>
    private const int PeriodWindowFrames = 40;

    /// <summary>
    /// Samples required before the window replaces the nominal fallback. A handful of frames can
    /// all land inside one burst, which would read as an implausibly short period.
    /// </summary>
    private const int MinimumPeriodSamples = 8;

    private readonly string _streamId = streamId;
    private readonly VideoCodec _codec = codec;
    private readonly uint _frameDuration = (uint)Math.Max(1, Math.Round(90_000d / nominalFrameRate));
    private readonly Queue<DateTimeOffset> _arrivals = new();
    private uint _timestamp;
    private DateTimeOffset? _previousReceivedAt;

    public VideoAccessUnit Create(EncodedVideoChunk chunk)
    {
        uint duration = this._frameDuration;
        if (this._previousReceivedAt is { } previous)
        {
            // A wall clock that moved backwards must not shrink the window below its real span.
            DateTimeOffset arrival = chunk.ReceivedAt < previous ? previous : chunk.ReceivedAt;
            this._arrivals.Enqueue(arrival);
            while (this._arrivals.Count > PeriodWindowFrames)
            {
                this._arrivals.Dequeue();
            }

            if (this._arrivals.Count >= MinimumPeriodSamples)
            {
                double mean = (arrival - this._arrivals.Peek()).TotalSeconds * 90_000 / (this._arrivals.Count - 1);
                if (mean >= 1)
                {
                    duration = (uint)Math.Clamp(Math.Round(mean), 1, uint.MaxValue);
                }
            }

            this._timestamp = unchecked(this._timestamp + duration);
        }

        this._previousReceivedAt = chunk.ReceivedAt;
        byte[] annexB = chunk.Data.ToArray();
        bool isKeyFrame = chunk.IsKeyFrame || AnnexBBitstream.IsKeyFrame(this._codec, annexB);
        bool hasParameters = chunk.ContainsCodecParameters || AnnexBBitstream.ContainsCodecParameters(this._codec, annexB);
        VideoAccessUnit unit = new(
            this._streamId,
            this._codec,
            annexB,
            chunk.Sequence,
            this._timestamp,
            duration,
            isKeyFrame,
            hasParameters);

        return unit;
    }
}
