using SIPSorcery.Net;

namespace MiCamera.Net.RTSP.Services;

internal sealed class WebRtcSession : IDisposable
{
    private int _mediaStarted;
    private bool _disposed;

    public WebRtcSession(string id, string streamId, RTCPeerConnection peer)
    {
        this.Id = id;
        this.StreamId = streamId;
        this.Peer = peer;
    }

    public string Id { get; }

    public string StreamId { get; }

    public RTCPeerConnection Peer { get; }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DisconnectedAt { get; set; }

    public bool HasAnswer { get; set; }

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
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        this.Cancellation.Cancel();
        this.Peer.Close("session closed");
        this.Cancellation.Dispose();
    }
}
