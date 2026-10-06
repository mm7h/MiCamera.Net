using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net;
using System.Diagnostics;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.Streams;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using MiCamera.Net.RTSP.Abstractions.Web;
using MiCamera.Net.Media.Services;
using MiCamera.Net.RTSP.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.Sys;
using SIPSorceryMedia.Abstractions;

namespace MiCamera.Net.RTSP.Services;

/// <summary>
/// Owns WebRTC peer connection state and keeps controller actions free of signaling and media lifecycle logic.
/// </summary>
public sealed class WebRtcSessionService : IDisposable
{
    private readonly ICameraStreamProvider _streams;
    private readonly INormalizedVideoStreamProvider _media;
    private readonly IMediaCapabilityProvider _mediaCapabilities;
    private readonly MiCameraRtspOptions _options;
    private readonly ILogger<WebRtcSessionService> _logger;
    private readonly ConcurrentDictionary<string, WebRtcSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _peerSlots = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource _creationCancellation = new();
    private TaskCompletionSource _creationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeCreations;
    private bool _paused;

    public WebRtcSessionService(
        ICameraStreamProvider streams,
        INormalizedVideoStreamProvider media,
        IMediaCapabilityProvider mediaCapabilities,
        MiCameraRtspOptions options,
        ILogger<WebRtcSessionService> logger)
    {
        this._streams = streams;
        this._media = media;
        this._mediaCapabilities = mediaCapabilities;
        this._options = options;
        this._logger = logger;
    }

    public async Task<IActionResult> CreateAsync(CreateWebRtcSessionRequest request, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (this._lifecycleLock)
        {
            if (this._paused || this._disposed) return ServiceUnavailable("Configuration is being applied. Please reconnect shortly.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._creationCancellation.Token);
            if (this._activeCreations == 0) this._creationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this._activeCreations++;
        }
        try { return await this.CreateCoreAsync(request, linked.Token).ConfigureAwait(false); }
        finally
        {
            linked.Dispose();
            lock (this._lifecycleLock)
            {
                if (--this._activeCreations == 0) this._creationsDrained.TrySetResult();
            }
        }
    }

    public async Task PauseAsync()
    {
        Task drained;
        lock (this._lifecycleLock)
        {
            this._paused = true;
            if (this._activeCreations == 0) this._creationsDrained.TrySetResult();
            drained = this._creationsDrained.Task;
        }
        this._creationCancellation.Cancel();
        await drained.ConfigureAwait(false);
        foreach (string id in this._sessions.Keys) this.Delete(id);
    }

    public void Resume()
    {
        lock (this._lifecycleLock)
        {
            this._creationCancellation.Dispose();
            this._creationCancellation = new();
            this._creationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            this._paused = false;
        }
    }

    private async Task<IActionResult> CreateCoreAsync(CreateWebRtcSessionRequest request, CancellationToken cancellationToken)
    {
        if (!this._options.WebRtc.Enabled)
        {
            return ServiceUnavailable("WebRTC is disabled.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.StreamId) ||
            !this._streams.Streams.Any(stream => string.Equals(stream.StreamId, request.StreamId, StringComparison.OrdinalIgnoreCase)))
        {
            return new NotFoundObjectResult(new { error = "The camera stream was not found." });
        }

        if (!this._mediaCapabilities.CanProvide(request.StreamId, VideoCodec.H264, out string? unavailableReason))
        {
            return ServiceUnavailable(unavailableReason ?? "H.264 output is unavailable for this camera stream.");
        }

        SemaphoreSlim slots = this._peerSlots.GetOrAdd(request.StreamId, _ => new SemaphoreSlim(this._options.WebRtc.MaxPeersPerStream));
        if (!slots.Wait(0))
        {
            return new ObjectResult(new { error = "The WebRTC peer limit for this camera stream has been reached." })
            {
                StatusCode = StatusCodes.Status429TooManyRequests
            };
        }

        string id = CreateSessionId();
        PortRange? ports = this._options.WebRtc.PortRangeStart is { } start && this._options.WebRtc.PortRangeEnd is { } end
            ? new PortRange(start, end) : null;
        RTCPeerConnection peer;
        try
        {
            peer = new CameraPeerConnection(this.CreateConfiguration(), ports, this._logger, request.StreamId);
        }
        catch (Exception)
        {
            slots.Release();
            return ServiceUnavailable("WebRTC UDP socket allocation failed. Check the bind address and available UDP port range.");
        }

        long lastRecovery = 0;
        void RequestRecovery()
        {
            lock (this._lifecycleLock)
            {
                if (this._paused || this._disposed) return;
                long now = Environment.TickCount64;
                long previous = Interlocked.Read(ref lastRecovery);
                if (now - previous >= 500 && Interlocked.CompareExchange(ref lastRecovery, now, previous) == previous)
                    this._media.RequestKeyFrame(request.StreamId);
            }
        }
        WebRtcSession session = new(id, request.StreamId, peer, RequestRecovery, () => slots.Release());
        peer.onconnectionstatechange += state => this.HandleConnectionStateChanged(session, state);
        peer.OnReceiveReport += (_, media, report) =>
        {
            if (media != SDPMediaTypesEnum.video || report.Feedback is not { } feedback) return;
            if (feedback.Header.PacketType == RTCPReportTypesEnum.PSFB &&
                feedback.Header.PayloadFeedbackMessageType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR)
            {
                RequestRecovery();
            }
        };

        // The media stack closes a peer on its own when something below WebRTC fails. Without these
        // the only trace of the cause is a bare "connection changed to closed".
        RtpIceChannel iceChannel = peer.GetRtpChannel();
        long stunReceived = 0, stunSent = 0, lastStunReceivedTicks = Environment.TickCount64;
        iceChannel.OnStunMessageReceived += (_, _, _) =>
        {
            Interlocked.Increment(ref stunReceived);
            Interlocked.Exchange(ref lastStunReceivedTicks, Environment.TickCount64);
        };
        iceChannel.OnStunMessageSent += (_, _, _) => Interlocked.Increment(ref stunSent);
        peer.oniceconnectionstatechange += state =>
            this._logger.LogInformation(
                "WebRTC ICE state for {StreamId} changed to {State}; stunIn={StunIn}, stunOut={StunOut}, stunIdle={StunIdleMs} ms.",
                request.StreamId, state, Interlocked.Read(ref stunReceived), Interlocked.Read(ref stunSent),
                Environment.TickCount64 - Interlocked.Read(ref lastStunReceivedTicks));
        peer.onicecandidateerror += (candidate, error) =>
            this._logger.LogWarning("WebRTC ICE candidate error for {StreamId}: {Error}.", request.StreamId, error);
        peer.OnTimeout += media =>
        {
            this._logger.LogWarning("WebRTC media timeout for {StreamId} on {Media}.", request.StreamId, media);
            if (media == SDPMediaTypesEnum.video && session.HasConnected &&
                this._sessions.TryRemove(session.Id, out WebRtcSession? expired))
                expired.Dispose();
        };
        peer.OnRtcpBye += reason =>
            this._logger.LogInformation("WebRTC RTCP BYE for {StreamId}: {Reason}.", request.StreamId, string.IsNullOrWhiteSpace(reason) ? "<none>" : reason);
        peer.OnRtpClosed += reason =>
            this._logger.LogWarning("WebRTC RTP channel closed for {StreamId}: {Reason}.", request.StreamId, string.IsNullOrWhiteSpace(reason) ? "<none>" : reason);

        try
        {
            string profile = string.Empty;
            if (this._streams.Streams.First(stream => string.Equals(stream.StreamId, request.StreamId, StringComparison.OrdinalIgnoreCase)).Codec == VideoCodec.H264)
            {
                if (!this._media.TryGetCodecParameters(request.StreamId, out VideoCodecParameters? parameters) ||
                    parameters is null || !TryReadH264Profile(parameters, out profile))
                {
                    session.Dispose();
                    return ServiceUnavailable("H.264 SPS parameters are not available yet. Wait for a camera key frame.");
                }
            }
            else
            {
                // Probe actual transcoder output before advertising a profile; camera resolution controls the level.
                using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probe.CancelAfter(this._options.WebRtc.PendingSessionTimeout);
                bool found = false;
                await foreach (VideoAccessUnit unit in this._media.SubscribeAsync(request.StreamId, VideoCodec.H264, probe.Token).ConfigureAwait(false))
                {
                    VideoCodecParameters parameters = new(request.StreamId, VideoCodec.H264, AnnexBBitstream.SplitNalUnits(unit.AnnexB));
                    if (TryReadH264Profile(parameters, out profile)) { found = true; break; }
                }
                if (!found)
                {
                    throw new InvalidOperationException("Transcoder produced no H.264 SPS.");
                }
            }

            VideoFormat h264 = new(VideoCodecsEnum.H264, 96, 90_000, $"packetization-mode=1;profile-level-id={profile};level-asymmetry-allowed=1");
            peer.addTrack(new MediaStreamTrack([h264], MediaStreamStatusEnum.SendOnly));
            RTCSessionDescriptionInit offer = peer.createOffer(null);
            // Raw SendVideo does not respond to feedback on behalf of our encoder.
            offer.sdp += "a=rtcp-fb:96 nack\r\na=rtcp-fb:96 nack pli\r\na=rtcp-fb:96 ccm fir\r\n";
            await peer.setLocalDescription(offer).ConfigureAwait(false);

            session.NegotiationStartedAt = DateTimeOffset.UtcNow;

            cancellationToken.ThrowIfCancellationRequested();
            if (!this._sessions.TryAdd(id, session))
            {
                session.Dispose();
                return ServiceUnavailable("Unable to create the WebRTC session.");
            }

            WebRtcSessionOfferResponse response = new(
                id,
                new WebRtcSessionDescriptionDto(offer.type.ToString().ToLowerInvariant(), offer.sdp),
                session.NegotiationStartedAt + this._options.WebRtc.PendingSessionTimeout);
            return new ObjectResult(response) { StatusCode = StatusCodes.Status201Created };
        }
        catch (Exception exception)
        {
            this._sessions.TryRemove(id, out _);
            session.Dispose();
            this._logger.LogWarning(exception, "Unable to create WebRTC offer for camera stream {StreamId}.", request.StreamId);
            return ServiceUnavailable("Unable to create a WebRTC offer for this camera stream.");
        }
    }

    public IActionResult SetAnswer(string sessionId, WebRtcSessionDescriptionDto answer)
    {
        if (!this._sessions.TryGetValue(sessionId, out WebRtcSession? session))
        {
            return new NotFoundObjectResult(new { error = "The WebRTC session was not found." });
        }

        if (!string.Equals(answer.Type, "answer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(answer.Sdp))
        {
            return new BadRequestObjectResult(new { error = "An SDP answer is required." });
        }

        SetDescriptionResultEnum result = session.Peer.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answer.Sdp
        });
        if (result != SetDescriptionResultEnum.OK)
        {
            return new BadRequestObjectResult(new { error = $"The SDP answer is incompatible: {result}." });
        }

        session.HasAnswer = true;
        return new NoContentResult();
    }

    public IActionResult AddIceCandidate(string sessionId, WebRtcIceCandidateDto candidate)
    {
        if (!this._sessions.TryGetValue(sessionId, out WebRtcSession? session))
        {
            return new NotFoundObjectResult(new { error = "The WebRTC session was not found." });
        }

        if (string.IsNullOrWhiteSpace(candidate.Candidate))
        {
            return new BadRequestObjectResult(new { error = "An ICE candidate is required." });
        }

        session.Peer.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate.Candidate,
            sdpMid = candidate.SdpMid,
            sdpMLineIndex = candidate.SdpMLineIndex ?? 0,
            usernameFragment = candidate.UsernameFragment
        });
        return new NoContentResult();
    }

    public IActionResult Delete(string sessionId)
    {
        if (!this._sessions.TryRemove(sessionId, out WebRtcSession? session))
        {
            return new NotFoundObjectResult(new { error = "The WebRTC session was not found." });
        }

        session.Dispose();
        return new NoContentResult();
    }

    public void RemoveExpiredSessions()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<string, WebRtcSession> pair in this._sessions)
        {
            WebRtcSession session = pair.Value;
            if (session.IsExpired(now, this._options.WebRtc))
            {
                if (this._sessions.TryRemove(pair.Key, out WebRtcSession? removed))
                {
                    this._logger.LogInformation("Removing expired WebRTC session for {StreamId}; connected={Connected}, disconnectedAt={DisconnectedAt}.",
                        session.StreamId, session.HasConnected, session.DisconnectedAt);
                    removed.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        foreach (WebRtcSession session in this._sessions.Values)
        {
            session.Dispose();
        }

        this._sessions.Clear();
    }

    private RTCConfiguration CreateConfiguration()
    {
        return new RTCConfiguration
        {
            iceServers = this._options.WebRtc.IceServers
                .Where(server => !string.IsNullOrWhiteSpace(server.Url))
                .Select(server => new RTCIceServer
                {
                    urls = server.Url,
                    username = server.Username,
                    credential = server.Credential
                })
                .ToList(),
            X_GatherTimeoutMs = (int)this._options.WebRtc.IceGatheringTimeout.TotalMilliseconds,
            X_BindAddress = this._options.WebRtc.BindAddress is { } address ? IPAddress.Parse(address) : null
        };
    }

    private void HandleConnectionStateChanged(WebRtcSession session, RTCPeerConnectionState state)
    {
        this._logger.LogInformation("WebRTC connection for {StreamId} changed to {State}.", session.StreamId, state);
        if (state == RTCPeerConnectionState.closed)
            this._logger.LogInformation("WebRTC {StreamId} retransmitted {Packets} packets.", session.StreamId, session.RetransmittedPackets);
        switch (state)
        {
            case RTCPeerConnectionState.connected:
                session.MarkConnected();
                session.EnableRetransmissions();
                session.StartMediaPump(() => this.PumpMediaAsync(session));
                break;
            case RTCPeerConnectionState.disconnected:
                session.DisconnectedAt ??= DateTimeOffset.UtcNow;
                break;
            case RTCPeerConnectionState.failed:
            case RTCPeerConnectionState.closed:
                if (this._sessions.TryRemove(session.Id, out WebRtcSession? removed))
                {
                    removed.Dispose();
                }

                break;
        }
    }

    private async Task PumpMediaAsync(WebRtcSession session)
    {
        try
        {
            ushort sequence = session.Peer.VideoLocalTrack.GetNextSeqNum();
            Stopwatch clock = Stopwatch.StartNew();
            VideoPlayoutScheduler playout = new(this._options.WebRtc.PlayoutDelay);
            // The schedule can only hold a unit it already has, so the receiver sees exactly the gap
            // with which the units reach this loop. Reporting both sides separates a starved pump
            // from a schedule that is holding frames for too long.
            double previousArrivalMs = 0, previousSendMs = 0, maxArrivalGapMs = 0, maxSendGapMs = 0, maxWaitMs = 0;
            long windowStartMs = clock.ElapsedMilliseconds;
            int units = 0, sends = 0, lateFrames = 0;
            long packets = 0;
            long? previousSequence = null;
            await foreach (VideoAccessUnit unit in this._media.SubscribeAsync(session.StreamId, VideoCodec.H264, session.Cancellation.Token)
                .ConfigureAwait(false))
            {
                // The relay delivers this stream in bursts separated by holes of hundreds of
                // milliseconds. Sending on arrival hands those holes to the receiver's jitter
                // buffer as freezes, so every unit waits for the slot its own timestamp gives it.
                double now = clock.Elapsed.TotalSeconds;
                if (units > 0)
                {
                    maxArrivalGapMs = Math.Max(maxArrivalGapMs, (now - previousArrivalMs) * 1000);
                }

                previousArrivalMs = now;
                units++;
                bool framesSkipped = previousSequence is { } previous && unit.SourceSequence > previous + 1;
                previousSequence = unit.SourceSequence;
                double wait = playout.WaitSeconds(now, unit.Timestamp90Khz, framesSkipped);
                if (wait < 0.001)
                {
                    lateFrames++;
                }
                else
                {
                    maxWaitMs = Math.Max(maxWaitMs, wait * 1000);
                    await Task.Delay(TimeSpan.FromSeconds(wait), session.Cancellation.Token).ConfigureAwait(false);
                }

                IReadOnlyList<byte[]> framePackets = RtpPacketizer.Packetize(unit, session.Peer.VideoLocalTrack.Ssrc, ref sequence, 1200);
                int burstSize = Math.Max(1, (framePackets.Count + 19) / 20);
                for (int index = 0; index < framePackets.Count; index++)
                {
                    // Spread each frame over at most 20 one-millisecond bursts. Key frames contain
                    // dozens of packets; a single burst stresses downstream receive queues even
                    // on a wired LAN. Leave room for reception and NACK repairs between bursts.
                    if (index > 0 && index % burstSize == 0)
                        await Task.Delay(1, session.Cancellation.Token).ConfigureAwait(false);
                    session.SendVideoPacket(framePackets[index]);
                    packets++;
                }

                double sentAt = clock.Elapsed.TotalSeconds;
                if (sends > 0)
                {
                    maxSendGapMs = Math.Max(maxSendGapMs, (sentAt - previousSendMs) * 1000);
                }

                previousSendMs = sentAt;
                sends++;
                if (clock.ElapsedMilliseconds - windowStartMs >= 30_000)
                {
                    this._logger.LogInformation(
                        "WebRTC playout {StreamId}: {Units} units, {Packets} packets, arrivalGapMax={ArrivalGap:F1} ms, sendGapMax={SendGap:F1} ms, late={Late}, waitMax={Wait:F1} ms, delay={Delay:F0} ms, nackMsgs={NackMsgs}, nackPackets={NackPackets}, resent={Resent}.",
                        session.StreamId, units, packets, maxArrivalGapMs, maxSendGapMs, lateFrames, maxWaitMs, playout.DelaySeconds * 1000,
                        session.NackMessages, session.NackPackets, session.RetransmittedPackets);
                    windowStartMs = clock.ElapsedMilliseconds;
                    units = sends = lateFrames = 0;
                    packets = 0;
                    maxArrivalGapMs = maxSendGapMs = maxWaitMs = 0;
                }
            }
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            this._logger.LogWarning(exception, "WebRTC media forwarding ended for camera stream {StreamId}.", session.StreamId);
            if (this._sessions.TryRemove(session.Id, out WebRtcSession? removed))
            {
                removed.Dispose();
            }
        }
    }

    private static string CreateSessionId()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class CameraPeerConnection(RTCConfiguration configuration, PortRange? ports,
        ILogger sessionLogger, string streamId) : RTCPeerConnection(configuration, portRange: ports)
    {
        public override void Close(string reason)
        {
            if (!this.IsClosed) sessionLogger.LogInformation("Closing WebRTC {StreamId}: {Reason}.", streamId, reason);
            base.Close(reason);
        }
    }

    internal static bool TryReadH264Profile(VideoCodecParameters parameters, out string profile)
    {
        foreach (ReadOnlyMemory<byte> nal in parameters.ParameterSets)
        {
            if (nal.Length >= 4 && (nal.Span[0] & 0x1f) == 7)
            {
                profile = Convert.ToHexString(nal.Span.Slice(1, 3)).ToLowerInvariant();
                return true;
            }
        }

        profile = string.Empty;
        return false;
    }

    private static IActionResult ServiceUnavailable(string message)
    {
        return new ObjectResult(new { error = message }) { StatusCode = StatusCodes.Status503ServiceUnavailable };
    }

}
