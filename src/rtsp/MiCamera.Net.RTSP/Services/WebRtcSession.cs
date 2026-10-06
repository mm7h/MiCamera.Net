using SIPSorcery.Net;

using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using System.Buffers.Binary;

namespace MiCamera.Net.RTSP.Services;

internal sealed class WebRtcSession : IDisposable
{
    private int _mediaStarted;
    private int _disposed;
    private readonly Action? _releasePeerSlot;
    private readonly Action _requestKeyFrame;
    /// <summary>
    /// Packets kept for repair. A lossy link asks for the same packet repeatedly, so the history has
    /// to outlast the receiver's whole recovery window rather than a single round trip.
    /// </summary>
    private const int PacketHistoryCapacity = 1024;

    /// <summary>Attempts allowed for one sequence number before the receiver is asked for a key frame instead.</summary>
    private const int MaximumRetransmissions = 8;

    /// <summary>How long after it was first sent a packet is still worth repairing.</summary>
    private const long PacketHistoryLifetimeMs = 5000;

    private readonly object _rtpSync = new();
    private readonly (byte[] Packet, byte[]? ProtectedPacket, long SentAt, int Retries)?[] _packetHistory =
        new (byte[], byte[]?, long, int)?[PacketHistoryCapacity];
    private bool _feedbackInstalled;
    private long _retransmittedPackets;
    private long _nackMessages;
    private long _nackPackets;
    private bool _senderClockStarted;
    private uint _senderTimestamp;
    private ulong _senderNtp;

    public WebRtcSession(string id, string streamId, RTCPeerConnection peer, Action requestKeyFrame, Action? releasePeerSlot = null)
    {
        this.Id = id;
        this.StreamId = streamId;
        this.Peer = peer;
        this._requestKeyFrame = requestKeyFrame;
        this._releasePeerSlot = releasePeerSlot;
    }

    public string Id { get; }

    public string StreamId { get; }

    public RTCPeerConnection Peer { get; }

    public long RetransmittedPackets => Interlocked.Read(ref this._retransmittedPackets);

    /// <summary>NACK feedback messages received from the peer.</summary>
    public long NackMessages => Interlocked.Read(ref this._nackMessages);

    /// <summary>Sequence numbers the peer asked to have resent.</summary>
    public long NackPackets => Interlocked.Read(ref this._nackPackets);

    public void EnableRetransmissions()
    {
        lock (this._rtpSync)
        {
            if (this._feedbackInstalled) return;
            // Preserve the negotiated DTLS/SRTP state and authentication/replay checks. Wrap the
            // actual receive context so every NACK FCI is visible before the library truncates it.
            // A video-only peer still has an audio PrimaryStream. Before an SSRC is mapped the
            // library uses that context; afterwards it uses VideoStream, so both must be observed.
            foreach (MediaStream stream in new[] { this.Peer.PrimaryStream, this.Peer.VideoStream }.Distinct())
            {
                SecureContext context = stream.GetSecurityContext();
                stream.SetSecurityContext((byte[] packet, int length, out int outputLength) =>
                    this.ProtectVideoPacket(context, packet, length, out outputLength), context.UnprotectRtpPacket,
                    (byte[] packet, int length, out int outputLength) =>
                    {
                        this.CorrectSenderReports(packet.AsSpan(0, length));
                        return context.ProtectRtcpPacket(packet, length, out outputLength);
                    }, (byte[] packet, int length, out int outputLength) =>
                    {
                        int result = context.UnprotectRtcpPacket(packet, length, out outputLength);
                        if (result == 0)
                            WebRtcFeedback.Handle(packet.AsSpan(0, outputLength), this.Peer.VideoLocalTrack.Ssrc,
                                sequence =>
                                {
                                    Interlocked.Increment(ref this._nackPackets);
                                    this.Retransmit(sequence);
                                }, this._requestKeyFrame, () => Interlocked.Increment(ref this._nackMessages));
                        return result;
                    });
            }
            this._feedbackInstalled = true;
        }
    }

    public void SendVideoPacket(byte[] packet)
    {
        lock (this._rtpSync)
        {
            uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4));
            if (!this._senderClockStarted || timestamp != this._senderTimestamp)
            {
                // Anchor at the first packet of a new frame. Pacing later packets and repairing
                // older frames must not move the RTP/NTP mapping used by sender reports.
                this._senderClockStarted = true;
                this._senderTimestamp = timestamp;
                this._senderNtp = RTCPSession.DateTimeToNtpTimestamp(DateTime.Now);
            }
            ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
            this._packetHistory[sequence % this._packetHistory.Length] = (packet, null, Environment.TickCount64, 0);
            this.SendPacketCore(packet);
        }
    }

    public void Retransmit(ushort sequence)
    {
        lock (this._rtpSync)
        {
            int slot = sequence % this._packetHistory.Length;
            if (this._packetHistory[slot] is not { } sent || sent.Retries >= MaximumRetransmissions ||
                Environment.TickCount64 - sent.SentAt > PacketHistoryLifetimeMs ||
                BinaryPrimitives.ReadUInt16BigEndian(sent.Packet.AsSpan(2)) != sequence)
            {
                // Only a packet that can no longer be repaired justifies a key frame. Requesting one
                // on every repeat NACK turns a lossy link into a flood of key frames, and each key
                // frame is far more data than the single packet the receiver actually asked for.
                this._requestKeyFrame();
                return;
            }

            this._packetHistory[slot] = (sent.Packet, sent.ProtectedPacket, sent.SentAt, sent.Retries + 1);
            this.SendPacketCore(sent.Packet);
            Interlocked.Increment(ref this._retransmittedPackets);
        }
    }

    private int ProtectVideoPacket(SecureContext context, byte[] packet, int length, out int outputLength)
    {
        lock (this._rtpSync)
        {
            int slot = length >= 12 ? BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)) % this._packetHistory.Length : -1;
            var sent = slot >= 0 ? this._packetHistory[slot] : null;
            bool matches = sent is { } entry &&
                BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)) == this.Peer.VideoLocalTrack.Ssrc &&
                packet.AsSpan(2, 6).SequenceEqual(entry.Packet.AsSpan(2, 6));
            if (matches && sent!.Value.ProtectedPacket is { } ciphertext)
            {
                // SIPSorcery 10.0.17 protects with the current ROC and increments it on every
                // sequence 65535. Re-protecting an old packet after wrap corrupts repairs and
                // can advance the new-packet ROC again. Resend the original wire bytes instead.
                ciphertext.CopyTo(packet, 0);
                outputLength = ciphertext.Length;
                return 0;
            }

            int result = context.ProtectRtpPacket(packet, length, out outputLength);
            if (matches && result == 0)
            {
                var original = sent!.Value;
                this._packetHistory[slot] = (original.Packet, packet.AsSpan(0, outputLength).ToArray(),
                    original.SentAt, original.Retries);
            }
            return result;
        }
    }

    private void SendPacketCore(byte[] packet) => this.Peer.SendRtpRaw(SDPMediaTypesEnum.video,
        packet.AsSpan(12).ToArray(), BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)),
        packet[1] >> 7, 96, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));

    private void CorrectSenderReports(Span<byte> packet)
    {
        lock (this._rtpSync)
        {
            if (!this._senderClockStarted) return;
            while (packet.Length >= 4)
            {
                int length = (BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) + 1) * 4;
                if (packet[0] >> 6 != 2 || length > packet.Length) return;
                Span<byte> report = packet[..length];
                packet = packet[length..];
                if (report[1] != 200 || report.Length < 28 ||
                    BinaryPrimitives.ReadUInt32BigEndian(report[4..]) != this.Peer.VideoLocalTrack.Ssrc) continue;
                // SIPSorcery 10.0.17 pairs current NTP with the last packet's RTP timestamp,
                // including retransmissions. RFC 3550 requires the RTP clock at that NTP time.
                // Signed fixed-point subtraction also handles a report built just before a
                // fresh packet acquired the lock, and the final 32-bit RTP value wraps normally.
                ulong ntp = BinaryPrimitives.ReadUInt64BigEndian(report[8..]);
                long elapsed = (long)(unchecked((long)(ntp - this._senderNtp)) / 4_294_967_296d * 90_000);
                BinaryPrimitives.WriteUInt32BigEndian(report[16..], unchecked(this._senderTimestamp + (uint)elapsed));
            }
        }
    }

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
