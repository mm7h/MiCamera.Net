using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using MiCamera.Net.RTSP.Services;
using Microsoft.Extensions.Logging;

namespace MiCamera.Net.RTSP.Server;

internal sealed class RtspClientConnection : IAsyncDisposable
{
    private const string Realm = "MiCamera.Net";
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly INormalizedVideoStreamProvider _media;
    private readonly MiCameraRtspOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly string _sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    private CancellationTokenSource? _playCancellation;
    private Task? _playTask;
    private string? _streamId;
    private ushort _sequenceNumber = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
    private readonly uint _ssrc = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
    private long _packetCount;
    private long _octetCount;
    private DateTimeOffset _lastSenderReport = DateTimeOffset.MinValue;

    public RtspClientConnection(
        TcpClient client,
        INormalizedVideoStreamProvider media,
        MiCameraRtspOptions options,
        ILogger logger)
    {
        this._client = client;
        this._client.NoDelay = true;
        this._stream = client.GetStream();
        this._media = media;
        this._options = options;
        this._logger = logger;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using BufferedStream reader = new(this._stream, 4096);
        using CancellationTokenSource idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            idleTimeout.CancelAfter(this._options.Rtsp.SessionTimeout);
            RtspRequest? request;
            try
            {
                request = await this.ReadRequestAsync(reader, idleTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && idleTimeout.IsCancellationRequested)
            {
                this._logger.LogDebug("RTSP 客户端会话 {SessionId} 的控制通道已闲置 {Timeout}，会话已过期。", this._sessionId, this._options.Rtsp.SessionTimeout);
                break;
            }

            if (request is null)
            {
                break;
            }

            if (!this.IsAuthorized(request))
            {
                await this.WriteResponseAsync(request, 401, "Unauthorized", new Dictionary<string, string>
                {
                    ["WWW-Authenticate"] = $"Digest realm=\"{Realm}\", nonce=\"{this._nonce}\", algorithm=MD5, qop=\"auth\""
                }, null, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await this.HandleRequestAsync(request, stoppingToken).ConfigureAwait(false);
            if (request.Method == "TEARDOWN")
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopPlaybackAsync().ConfigureAwait(false);
        this._stream.Dispose();
        this._client.Dispose();
        this._writeLock.Dispose();
    }

    private async Task HandleRequestAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "OPTIONS":
                await this.WriteResponseAsync(request, 200, "OK", new Dictionary<string, string>
                {
                    ["Public"] = "OPTIONS, DESCRIBE, SETUP, PLAY, PAUSE, GET_PARAMETER, TEARDOWN"
                }, null, cancellationToken).ConfigureAwait(false);
                return;

            case "DESCRIBE":
                await this.HandleDescribeAsync(request, cancellationToken).ConfigureAwait(false);
                return;

            case "SETUP":
                await this.HandleSetupAsync(request, cancellationToken).ConfigureAwait(false);
                return;

            case "PLAY":
                await this.HandlePlayAsync(request, cancellationToken).ConfigureAwait(false);
                return;

            case "PAUSE":
                await this.StopPlaybackAsync().ConfigureAwait(false);
                await this.WriteResponseAsync(request, 200, "OK", this.SessionHeaders(), null, cancellationToken).ConfigureAwait(false);
                return;

            case "GET_PARAMETER":
                await this.WriteResponseAsync(request, 200, "OK", this.SessionHeaders(), null, cancellationToken).ConfigureAwait(false);
                return;

            case "TEARDOWN":
                await this.StopPlaybackAsync().ConfigureAwait(false);
                await this.WriteResponseAsync(request, 200, "OK", this.SessionHeaders(), null, cancellationToken).ConfigureAwait(false);
                return;

            default:
                await this.WriteResponseAsync(request, 405, "Method Not Allowed", null, null, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task HandleDescribeAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        string? streamId = this.GetStreamId(request.Uri);
        if (streamId is null || !this._media.TryGetCodecParameters(streamId, out VideoCodecParameters? parameters))
        {
            await this.WriteResponseAsync(request, 503, "Service Unavailable", null, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        string sdp = this.CreateSdp(request.Uri, parameters!);
        await this.WriteResponseAsync(request, 200, "OK", new Dictionary<string, string>
        {
            ["Content-Base"] = request.Uri,
            ["Content-Type"] = "application/sdp"
        }, Encoding.UTF8.GetBytes(sdp), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSetupAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        string? streamId = this.GetStreamId(request.Uri);
        if (streamId is null)
        {
            await this.WriteResponseAsync(request, 404, "Not Found", null, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!request.Headers.TryGetValue("Transport", out string? transport) ||
            !transport.Contains("RTP/AVP/TCP", StringComparison.OrdinalIgnoreCase))
        {
            await this.WriteResponseAsync(request, 461, "Unsupported Transport", null, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        this._streamId = streamId;
        Dictionary<string, string> headers = this.SessionHeaders();
        headers["Transport"] = "RTP/AVP/TCP;unicast;interleaved=0-1";
        await this.WriteResponseAsync(request, 200, "OK", headers, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlayAsync(RtspRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(this._streamId))
        {
            await this.WriteResponseAsync(request, 455, "Method Not Valid in This State", null, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        await this.StopPlaybackAsync().ConfigureAwait(false);
        this._playCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The shared live clock does not start at zero for a new viewer. Omit optional RTP-Info
        // until the first unit is known, and finish the PLAY response before any media is written.
        await this.WriteResponseAsync(request, 200, "OK", this.SessionHeaders(), null, cancellationToken).ConfigureAwait(false);
        this._playTask = this.SendMediaAsync(this._streamId, this._playCancellation.Token);
    }

    private async Task SendMediaAsync(string streamId, CancellationToken cancellationToken)
    {
        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            // Hold a bounded relay buffer for RTSP too, rather than passing camera delivery
            // bursts directly to the client's decode and render queues.
            VideoPlayoutScheduler playout = new(TimeSpan.FromMilliseconds(1200));
            long? previousSequence = null;
            await foreach (VideoAccessUnit unit in this._media.SubscribeAsync(streamId, null, cancellationToken)
                .ConfigureAwait(false))
            {
                bool framesSkipped = previousSequence is { } previous && unit.SourceSequence > previous + 1;
                previousSequence = unit.SourceSequence;
                double wait = playout.WaitSeconds(clock.Elapsed.TotalSeconds, unit.Timestamp90Khz, framesSkipped);
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);
                }

                long frameStarted = Stopwatch.GetTimestamp();
                IReadOnlyList<byte[]> packets = RtpPacketizer.Packetize(unit, this._ssrc, ref this._sequenceNumber, this._options.Rtsp.RtpMtu);
                foreach (byte[] packet in packets)
                {
                    Interlocked.Increment(ref this._packetCount);
                    Interlocked.Add(ref this._octetCount, packet.Length - 12);
                    await this.WriteInterleavedAsync(0, packet, cancellationToken).ConfigureAwait(false);
                }

                if (DateTimeOffset.UtcNow - this._lastSenderReport >= TimeSpan.FromSeconds(5))
                {
                    this._lastSenderReport = DateTimeOffset.UtcNow;
                    uint reportTimestamp = unchecked(unit.Timestamp90Khz +
                        (uint)(Stopwatch.GetElapsedTime(frameStarted).TotalSeconds * 90_000));
                    await this.WriteInterleavedAsync(1, this.CreateSenderReport(reportTimestamp), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            this._logger.LogDebug(exception, "摄像头流 {StreamId} 的 RTSP 媒体发送已结束。", streamId);
        }
    }

    private async Task StopPlaybackAsync()
    {
        if (this._playCancellation is null)
        {
            return;
        }

        this._playCancellation.Cancel();
        try
        {
            if (this._playTask is not null)
            {
                await this._playTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            this._playCancellation.Dispose();
            this._playCancellation = null;
            this._playTask = null;
        }
    }

    private async Task WriteInterleavedAsync(byte channel, byte[] payload, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[payload.Length + 4];
        frame[0] = (byte)'$';
        frame[1] = channel;
        frame[2] = (byte)(payload.Length >> 8);
        frame[3] = (byte)payload.Length;
        payload.CopyTo(frame, 4);
        await this.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private byte[] CreateSenderReport(uint rtpTimestamp)
    {
        byte[] report = new byte[28];
        report[0] = 0x80;
        report[1] = 200;
        report[3] = 6;
        WriteUInt32(report, 4, this._ssrc);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long milliseconds = now.ToUnixTimeMilliseconds();
        // Split seconds and fraction before scaling: epoch milliseconds times 2^32 overflows ulong.
        WriteUInt32(report, 8, unchecked((uint)((milliseconds / 1000) + 2_208_988_800L)));
        WriteUInt32(report, 12, (uint)((ulong)(milliseconds % 1000) * 4_294_967_296UL / 1000));
        WriteUInt32(report, 16, rtpTimestamp);
        WriteUInt32(report, 20, (uint)Interlocked.Read(ref this._packetCount));
        WriteUInt32(report, 24, (uint)Interlocked.Read(ref this._octetCount));
        return report;
    }

    private static void WriteUInt32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private async Task WriteResponseAsync(
        RtspRequest request,
        int statusCode,
        string reason,
        IReadOnlyDictionary<string, string>? headers,
        byte[]? body,
        CancellationToken cancellationToken)
    {
        StringBuilder response = new($"RTSP/1.0 {statusCode} {reason}\r\n");
        if (request.Headers.TryGetValue("CSeq", out string? cseq))
        {
            response.Append("CSeq: ").Append(cseq).Append("\r\n");
        }

        response.Append("Server: MiCamera.Net\r\n");
        if (headers is not null)
        {
            foreach ((string key, string value) in headers)
            {
                response.Append(key).Append(": ").Append(value).Append("\r\n");
            }
        }

        response.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n\r\n");
        byte[] head = Encoding.ASCII.GetBytes(response.ToString());
        if (body is not null && body.Length > 0)
        {
            // A response header and SDP body must be atomic with respect to interleaved RTP.
            byte[] message = new byte[head.Length + body.Length];
            head.CopyTo(message, 0);
            body.CopyTo(message, head.Length);
            await this.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await this.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        await this._writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this._stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await this._stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this._writeLock.Release();
        }
    }

    private bool IsAuthorized(RtspRequest request)
    {
        if (this._options.Rtsp.WebManaged && string.IsNullOrEmpty(this._options.Rtsp.DigestHa1))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(this._options.Rtsp.Username) && string.IsNullOrWhiteSpace(this._options.Rtsp.Password) && this._options.Rtsp.DigestHa1 is null)
        {
            return true;
        }

        if (!request.Headers.TryGetValue("Authorization", out string? value) ||
            !value.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Dictionary<string, string> values = ParseDigest(value[7..]);
        if (!values.TryGetValue("username", out string? username) ||
            !values.TryGetValue("realm", out string? realm) ||
            !values.TryGetValue("nonce", out string? nonce) ||
            !values.TryGetValue("uri", out string? uri) ||
            !values.TryGetValue("response", out string? response) ||
            !string.Equals(username, this._options.Rtsp.Username, StringComparison.Ordinal) ||
            !string.Equals(realm, Realm, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(nonce), Encoding.ASCII.GetBytes(this._nonce)))
        {
            return false;
        }

        string ha1 = this._options.Rtsp.DigestHa1 ?? Md5($"{username}:{Realm}:{this._options.Rtsp.Password}");
        string ha2 = Md5($"{request.Method}:{uri}");
        string expected;
        if (values.TryGetValue("qop", out string? qop) &&
            values.TryGetValue("nc", out string? nc) &&
            values.TryGetValue("cnonce", out string? cnonce))
        {
            expected = Md5($"{ha1}:{this._nonce}:{nc}:{cnonce}:{qop}:{ha2}");
        }
        else
        {
            expected = Md5($"{ha1}:{this._nonce}:{ha2}");
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(response), Encoding.ASCII.GetBytes(expected));
    }

    private string? GetStreamId(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? value))
        {
            return null;
        }

        string prefix = this._options.Rtsp.PathPrefix.TrimEnd('/');
        string path = value.AbsolutePath.TrimEnd('/');
        if (!path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string streamId = Uri.UnescapeDataString(path[(prefix.Length + 1)..]);
        int trackSeparator = streamId.IndexOf('/');
        if (trackSeparator >= 0)
        {
            string track = streamId[(trackSeparator + 1)..];
            if (!string.Equals(track, "trackID=0", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            streamId = streamId[..trackSeparator];
        }

        return string.IsNullOrWhiteSpace(streamId) || streamId.Contains('/') ? null : streamId;
    }

    private string CreateSdp(string requestUri, VideoCodecParameters parameters)
    {
        string attributes;
        if (parameters.Codec == VideoCodec.H264)
        {
            if (!TryGetParameterSet(parameters, 7, out ReadOnlyMemory<byte> sps) ||
                !TryGetParameterSet(parameters, 8, out ReadOnlyMemory<byte> pps))
            {
                throw new InvalidOperationException("H.264 编码参数不完整。");
            }

            ReadOnlySpan<byte> spsBytes = sps.Span;
            string profile = spsBytes.Length >= 4
                ? Convert.ToHexString(spsBytes.Slice(1, 3)).ToLowerInvariant()
                : "42e01f";
            attributes = string.Concat(
                "a=rtpmap:96 H264/90000\r\n",
                "a=fmtp:96 packetization-mode=1;profile-level-id=", profile,
                ";sprop-parameter-sets=", Convert.ToBase64String(sps.ToArray()), ",", Convert.ToBase64String(pps.ToArray()), "\r\n");
        }
        else
        {
            if (!TryGetParameterSet(parameters, 32, out ReadOnlyMemory<byte> vps) ||
                !TryGetParameterSet(parameters, 33, out ReadOnlyMemory<byte> sps) ||
                !TryGetParameterSet(parameters, 34, out ReadOnlyMemory<byte> pps))
            {
                throw new InvalidOperationException("H.265 编码参数不完整。");
            }

            attributes = string.Concat(
                "a=rtpmap:96 H265/90000\r\n",
                "a=fmtp:96 sprop-vps=", Convert.ToBase64String(vps.ToArray()),
                ";sprop-sps=", Convert.ToBase64String(sps.ToArray()),
                ";sprop-pps=", Convert.ToBase64String(pps.ToArray()), "\r\n");
        }

        return string.Concat(
            "v=0\r\n",
            "o=- 0 0 IN IP4 127.0.0.1\r\n",
            "s=MiCamera.Net\r\n",
            "t=0 0\r\n",
            "a=control:*\r\n",
            "m=video 0 RTP/AVP 96\r\n",
            "c=IN IP4 0.0.0.0\r\n",
            "a=control:", requestUri, "/trackID=0\r\n",
            attributes);
    }

    private Dictionary<string, string> SessionHeaders() => new()
    {
        ["Session"] = $"{this._sessionId};timeout={(int)Math.Ceiling(this._options.Rtsp.SessionTimeout.TotalSeconds)}"
    };

    private async Task<RtspRequest?> ReadRequestAsync(Stream reader, CancellationTokenSource idleTimeout)
    {
        CancellationToken cancellationToken = idleTimeout.Token;
        byte[] first = new byte[1];
        while (await reader.ReadAsync(first, cancellationToken).ConfigureAwait(false) != 0)
        {
            if (first[0] == (byte)'$')
            {
                // RFC 2326 interleaving is binary: channel, big-endian length, then that many bytes.
                // RTCP can contain newlines and must never be decoded as an RTSP request.
                byte[] frameHeader = new byte[3];
                await reader.ReadExactlyAsync(frameHeader, cancellationToken).ConfigureAwait(false);
                int length = (frameHeader[1] << 8) | frameHeader[2];
                byte[] feedback = new byte[length];
                await reader.ReadExactlyAsync(feedback, cancellationToken).ConfigureAwait(false);
                idleTimeout.CancelAfter(this._options.Rtsp.SessionTimeout);
                continue;
            }

            List<byte> head = [first[0]];
            while (head.Count < 16_384)
            {
                await reader.ReadExactlyAsync(first, cancellationToken).ConfigureAwait(false);
                head.Add(first[0]);
                int count = head.Count;
                if (count >= 4 && head[count - 4] == 13 && head[count - 3] == 10 &&
                    head[count - 2] == 13 && head[count - 1] == 10)
                {
                    break;
                }
            }
            if (head.Count >= 16_384)
            {
                throw new InvalidDataException("RTSP 请求头过大。");
            }

            string[] lines = Encoding.ASCII.GetString([.. head]).Split("\r\n");
            string[] parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[2] != "RTSP/1.0")
            {
                throw new InvalidDataException("RTSP 请求行无效。");
            }

            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int separator = line.IndexOf(':');
                if (separator > 0)
                {
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

            if (headers.TryGetValue("Content-Length", out string? value))
            {
                if (!int.TryParse(value, out int length) || length is < 0 or > 65_536)
                {
                    throw new InvalidDataException("RTSP 请求体长度无效。");
                }

                byte[] body = new byte[length];
                await reader.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
            }
            return new RtspRequest(parts[0].ToUpperInvariant(), parts[1], headers);
        }
        return null;
    }

    private static Dictionary<string, string> ParseDigest(string source)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string item in source.Split(','))
        {
            int separator = item.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = item[..separator].Trim();
            string value = item[(separator + 1)..].Trim().Trim('"');
            result[key] = value;
        }

        return result;
    }

    private static string Md5(string value)
    {
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static bool TryGetParameterSet(VideoCodecParameters parameters, int type, out ReadOnlyMemory<byte> value)
    {
        foreach (ReadOnlyMemory<byte> parameterSet in parameters.ParameterSets)
        {
            if (!parameterSet.IsEmpty &&
                (parameters.Codec == VideoCodec.H264
                    ? (parameterSet.Span[0] & 0x1f) == type
                    : ((parameterSet.Span[0] >> 1) & 0x3f) == type))
            {
                value = parameterSet;
                return true;
            }
        }

        value = default;
        return false;
    }

}
