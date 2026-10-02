using SIPSorcery.Net;

using MiCamera.Net.RTSP.Abstractions.ConfigSettings;

namespace MiCamera.Net.RTSP.Services;

internal sealed class WebRtcSession : IDisposable
{
    private int _mediaStarted;
    private int _disposed;
    private readonly Action? _releasePeerSlot;

    public WebRtcSession(string id, string streamId, RTCPeerConnection peer, Action? releasePeerSlot = null)
    {
        this.Id = id;
        this.StreamId = streamId;
        this.Peer = peer;
        this._releasePeerSlot = releasePeerSlot;
    }

    public string Id { get; }

    public string StreamId { get; }

    public RTCPeerConnection Peer { get; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset NegotiationStartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DisconnectedAt { get; set; }

    public bool HasAnswer { get; set; }

    public bool HasConnected { get; set; }

    public bool IsExpired(DateTimeOffset now, WebRtcOptions options) =>
        (!this.HasConnected && now - this.NegotiationStartedAt >= options.PendingSessionTimeout) ||
        (this.DisconnectedAt is { } disconnected && now - disconnected >= options.DisconnectedGracePeriod);

    public void MarkConnected()
    {
        this.HasConnected = true;
        this.DisconnectedAt = null;
    }

    public CancellationTokenSource Cancellation { get; } = new();

    public void StartMediaPump(Func<Task> start)
    {
        if (Interlocked.Exchange(ref this._mediaStarted, 1) == 0)
        {
            _ = start();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this._disposed, 1) == 1)
        {
            return;
        }

        try
        {
            this.Cancellation.Cancel();
            this.Peer.Close("session closed");
        }
        finally
        {
            this.Cancellation.Dispose();
            this._releasePeerSlot?.Invoke();
        }
    }
}
