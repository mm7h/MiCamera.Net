using System.Runtime.CompilerServices;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.Common.Models;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Abstractions.Streams;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using MiCamera.Net.RTSP.Abstractions.Web;
using MiCamera.Net.RTSP.Controllers;
using MiCamera.Net.RTSP.Extensions;
using MiCamera.Net.RTSP.Security;
using MiCamera.Net.RTSP.Services;
using MiCamera.Net.Server.Protocol.Miloco;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using SIPSorcery.Net;
using Xunit;

namespace MiCamera.Net.Tests;

public sealed class DeploymentTests
{
    [Fact]
    public void AcceptsActualLegacyLoginStatus() => MilocoResponseValidator.EnsureXiaomiAccountAuthorized(
        "{\"code\":0,\"data\":{\"is_logged_in\":true}}");

    [Theory]
    [InlineData("{\"code\":0,\"data\":{\"is_logged_in\":false}}")]
    [InlineData("{\"code\":0,\"data\":true}")]
    [InlineData("{\"code\":0,\"data\":{\"is_logged_in\":\"true\"}}")]
    [InlineData("{\"code\":1,\"data\":{\"is_logged_in\":true}}")]
    [InlineData("{\"code\":\"0\",\"data\":{\"is_logged_in\":true}}")]
    [InlineData("not-json")]
    public void RejectsFalseUnknownAndBusinessErrorStatus(string response) =>
        Assert.Throws<MilocoAuthenticationException>(() => MilocoResponseValidator.EnsureXiaomiAccountAuthorized(response));

    [Fact]
    public void AuthenticationErrorsDoNotEchoRemoteBodies()
    {
        string marker = Guid.NewGuid().ToString();
        var error = Assert.Throws<MilocoAuthenticationException>(() => MilocoResponseValidator.ReadData(
            "{\"code\":1,\"message\":\"" + marker + "\",\"data\":null}"));
        Assert.DoesNotContain(marker, error.ToString());
    }

    [Theory]
    [InlineData(50001, 50100)]
    [InlineData(50100, 50000)]
    [InlineData(0, 50100)]
    [InlineData(50000, 65536)]
    [InlineData(50000, null)]
    public void RejectsInvalidWebRtcPortRanges(int? start, int? end)
    {
        var options = new MiCameraRtspOptions();
        options.WebRtc.PortRangeStart = start;
        options.WebRtc.PortRangeEnd = end;
        Assert.Throws<ArgumentException>(() => MiCameraRtspOptionsValidator.Validate(options));
    }

    [Fact]
    public void SupportsExistingDefaultsAndDeploymentRange()
    {
        var options = new MiCameraRtspOptions();
        MiCameraRtspOptionsValidator.Validate(options);
        options.WebRtc.BindAddress = "192.168.1.100";
        options.WebRtc.PortRangeStart = 50000;
        options.WebRtc.PortRangeEnd = 50100;
        MiCameraRtspOptionsValidator.Validate(options);
    }

    [Fact]
    public void NonLoopbackAuthenticationRequirementsRemainEnforced()
    {
        var options = new MiCameraRtspOptions();
        options.Http.ListenUrl = "http://192.168.1.100:5080";
        Assert.Throws<ArgumentException>(() => MiCameraRtspOptionsValidator.Validate(options));
        options.Http.BearerToken = Guid.NewGuid().ToString();
        options.Rtsp.ListenAddress = "192.168.1.100";
        Assert.Throws<ArgumentException>(() => MiCameraRtspOptionsValidator.Validate(options));
    }

    [Fact]
    public void AnswerWithoutConnectionStillExpires()
    {
        using var session = new WebRtcSession("id", "camera", new RTCPeerConnection());
        session.HasAnswer = true;
        Assert.True(session.IsExpired(session.NegotiationStartedAt + TimeSpan.FromSeconds(31), new WebRtcOptions()));
    }

    [Fact]
    public void ReconnectingClearsDisconnectedTime()
    {
        using var session = new WebRtcSession("id", "camera", new RTCPeerConnection());
        session.DisconnectedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
        session.MarkConnected();
        Assert.Null(session.DisconnectedAt);
        Assert.False(session.IsExpired(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1), new WebRtcOptions()));
    }

    [Fact]
    public void SessionDisposeReleasesPeerReservationExactlyOnce()
    {
        int releases = 0;
        var session = new WebRtcSession("id", "camera", new RTCPeerConnection(), () => Interlocked.Increment(ref releases));
        Parallel.Invoke(session.Dispose, session.Dispose);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void ReadsActualSourceH264ProfileInsteadOfAdvertisingBaseline()
    {
        var parameters = new VideoCodecParameters("camera", VideoCodec.H264, new ReadOnlyMemory<byte>[] { new byte[] { 0x67, 0x64, 0x00, 0x28 } });
        Assert.True(WebRtcSessionService.TryReadH264Profile(parameters, out string profile));
        Assert.Equal("640028", profile);
    }

    [Fact]
    public void LiveIsDistinctFromOfflineReadiness()
    {
        var controller = new HealthController(new OfflineSource(), new NoMedia(), new NoMedia(), new MiCameraServerOptions(), new MiCameraRtspOptions());
        Assert.IsType<OkObjectResult>(controller.Live());
        var ready = Assert.IsType<ObjectResult>(controller.Ready());
        Assert.Equal(503, ready.StatusCode);
        var response = Assert.IsType<HealthResponse>(ready.Value);
        Assert.False(response.Ready);
        Assert.False(response.Streams.Single().Receiving);
    }

    [Fact]
    public void MissingMediaReportsSnapshotUnavailableWithoutStoppingCore()
    {
        var controller = new CameraApiService(new OfflineSource(), new NoMedia(), new NoMedia(), new MiCameraRtspOptions());
        var response = Assert.IsType<ObjectResult>(controller.GetSnapshot("camera"));
        Assert.Equal(503, response.StatusCode);
        Assert.Contains("FFmpeg", System.Text.Json.JsonSerializer.Serialize(response.Value));
    }

    [Fact]
    public void HealthRequestsUseTheSameBearerAuthorization()
    {
        var options = new MiCameraRtspOptions();
        options.Http.BearerToken = Guid.NewGuid().ToString();
        var filter = new BearerTokenAuthorizationFilter(options);
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/health/live";
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new AuthorizationFilterContext(action, []);
        filter.OnAuthorization(context);
        Assert.IsType<UnauthorizedResult>(context.Result);
        http.Request.Headers.Authorization = "Bearer " + options.Http.BearerToken;
        context = new AuthorizationFilterContext(action, []);
        filter.OnAuthorization(context);
        Assert.Null(context.Result);
    }

    private sealed class NoMedia : IVideoSnapshotProvider, IMediaCapabilityProvider
    {
        public bool TryGetSnapshot(string id, out VideoSnapshot? snapshot) { snapshot = null; return false; }
        public bool CanProvide(string id, VideoCodec codec, out string? reason) { reason = "Unavailable"; return false; }
    }

    private sealed class OfflineSource : ICameraStreamProvider
    {
        public IReadOnlyCollection<CameraStreamDescriptor> Streams { get; } = [new("camera", "device", 0, VideoCodec.H265)];
        public bool TryGetSnapshot(string id, out CameraStreamSnapshot? snapshot)
        { snapshot = new(id, CameraStreamState.Reconnecting, 0, null, null); return true; }
        public async IAsyncEnumerable<EncodedVideoChunk> SubscribeAsync(string id, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }
}
