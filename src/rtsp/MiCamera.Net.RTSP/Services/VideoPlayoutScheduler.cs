namespace MiCamera.Net.RTSP.Services;

/// <summary>
/// Releases each access unit on the sender's own clock using the source's 90 kHz cadence.
/// The camera is reached through a relay that delivers frames in bursts separated by holes of
/// hundreds of milliseconds. Forwarding every unit the moment it arrives reproduces those holes in
/// the receiver's jitter buffer as freezes, so each unit waits for the slot its own timestamp gives
/// it, offset by a playout delay that has to absorb the holes. A unit that turns up later than its
/// slot raises the delay immediately; reception that keeps up hands the surplus back gradually, so
/// the delay settles on the smallest value that currently works instead of on a fixed guess.
/// </summary>
internal sealed class VideoPlayoutScheduler(TimeSpan playoutDelay)
{
    /// <summary>
    /// Maximum hold and recovery delay. A forward jump after dropped frames must not cause another
    /// long sleep: the bounded subscriber queue would fill and drop the next reference frames too.
    /// </summary>
    private const double DiscontinuitySeconds = 2;

    /// <summary>Share of the playout delay handed back per second of reception that keeps up.</summary>
    private const double RecoveryRate = 0.05;

    private readonly double _minimumDelay = Math.Clamp(playoutDelay.TotalSeconds, 0, DiscontinuitySeconds);
    private uint _origin;
    private double _startedAt;
    private double _lastCall;
    private bool _started;

    /// <summary>The delay the schedule currently holds each unit for, in seconds.</summary>
    public double DelaySeconds { get; private set; }

    /// <summary>Seconds to hold the unit back; zero means it is due now.</summary>
    public double WaitSeconds(double nowSeconds, uint timestamp90Khz, bool framesSkipped = false)
    {
        if (!this._started)
        {
            this._started = true;
            this._origin = timestamp90Khz;
            this._startedAt = nowSeconds;
            this._lastCall = nowSeconds;
            this.DelaySeconds = this._minimumDelay;
            return this._minimumDelay;
        }

        // The 32-bit counter wraps after 13 hours, and unsigned subtraction keeps the difference
        // correct as long as a session is shorter than half a wrap.
        double ideal = unchecked((uint)(timestamp90Khz - this._origin)) / 90_000d;
        double wait = this.DelaySeconds - (nowSeconds - this._startedAt) + ideal;
        double sinceLastCall = nowSeconds - this._lastCall;
        this._lastCall = nowSeconds;

        if (framesSkipped || wait > DiscontinuitySeconds)
        {
            // The missing frames cannot be played. Send the recovered frame now, then pace from
            // its new clock anchor instead of waiting for the discarded part of the timeline.
            this._origin = timestamp90Khz;
            this._startedAt = nowSeconds - this._minimumDelay;
            this.DelaySeconds = this._minimumDelay;
            return 0;
        }

        if (wait < -DiscontinuitySeconds)
        {
            this._origin = timestamp90Khz;
            this._startedAt = nowSeconds;
            this.DelaySeconds = this._minimumDelay;
            return this._minimumDelay;
        }

        this.DelaySeconds = wait < 0
            // Reception fell behind the schedule. Give the delay the missing time instead of
            // repaying the deficit as a burst of packets the receiver cannot play back in time.
            ? Math.Min(DiscontinuitySeconds, this.DelaySeconds - wait)
            : Math.Max(this._minimumDelay, this.DelaySeconds * (1 - Math.Min(1, sinceLastCall * RecoveryRate)));
        return Math.Max(0, wait);
    }
}
