using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.Streams;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using MiCamera.Net.RTSP.Abstractions.Web;
using MiCamera.Net.Media.Services;
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
            peer = new(this.CreateConfiguration(), portRange: ports);
        }
        catch (Exception)
        {
            slots.Release();
            return ServiceUnavailable("WebRTC UDP socket allocation failed. Check the bind address and available UDP port range.");
        }

        WebRtcSession session = new(id, request.StreamId, peer, () => slots.Release());
        peer.onconnectionstatechange += state => this.HandleConnectionStateChanged(session, state);

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
            await peer.setLocalDescription(offer).ConfigureAwait(false);

            session.NegotiationStartedAt = DateTimeOffset.UtcNow;

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
        switch (state)
        {
            case RTCPeerConnectionState.connected:
                session.MarkConnected();
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
            await foreach (VideoAccessUnit unit in this._media.SubscribeAsync(session.StreamId, VideoCodec.H264, session.Cancellation.Token)
                .ConfigureAwait(false))
            {
                session.Peer.SendVideo(unit.Duration90Khz, unit.AnnexB.ToArray());
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
