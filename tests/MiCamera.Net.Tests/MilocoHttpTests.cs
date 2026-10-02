using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Server.Protocol.Miloco;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MiCamera.Net.Tests;

public sealed class MilocoHttpTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UsesCookieAndActualBusinessStatusInsteadOfHttp200(bool authorized)
    {
        string cookie = Guid.NewGuid().ToString("N");
        await using WebApplication app = CreateApplication();
        app.MapPost("/api/auth/login", (HttpContext context) =>
        {
            context.Response.Cookies.Append("access_token", cookie);
            return Results.Json(new { code = 0, data = new { username = "admin" } });
        });
        app.MapGet("/api/miot/login_status", (HttpContext context) =>
            context.Request.Cookies["access_token"] == cookie
                ? Results.Json(new { code = 0, data = new { is_logged_in = authorized } })
                : Results.Unauthorized());
        await app.StartAsync();
        using MilocoSessionClient client = CreateClient(app);
        if (authorized)
            await client.EnsureAuthenticatedAsync(CancellationToken.None);
        else
        {
            var error = await Assert.ThrowsAsync<MilocoAuthenticationException>(() => client.EnsureAuthenticatedAsync(CancellationToken.None));
            Assert.Contains("尚未授权", error.Message);
        }
    }

    [Fact]
    public async Task HttpFailureDoesNotIncludeRemoteSecretInException()
    {
        string sensitiveBody = Guid.NewGuid().ToString();
        await using WebApplication app = CreateApplication();
        app.MapPost("/api/auth/login", () => Results.Text(sensitiveBody, statusCode: 401));
        await app.StartAsync();
        using MilocoSessionClient client = CreateClient(app);
        var error = await Assert.ThrowsAsync<MilocoAuthenticationException>(() => client.EnsureAuthenticatedAsync(CancellationToken.None));
        Assert.Contains("本地登录被拒绝", error.Message);
        Assert.DoesNotContain(sensitiveBody, error.ToString());
    }

    [Theory]
    [InlineData(SocketError.HostNotFound, "无法解析 Miloco 主机名")]
    [InlineData(SocketError.ConnectionRefused, "Miloco 拒绝了连接")]
    public void TransportFailuresProvideSafeActionableGuidance(SocketError socketError, string expectedMessage)
    {
        string sensitiveDetail = Guid.NewGuid().ToString("N");
        var error = MilocoSessionClient.CreateAuthenticationFailure(
            new HttpRequestException(sensitiveDetail, new SocketException((int)socketError)));

        Assert.Contains(expectedMessage, error.Message);
        Assert.DoesNotContain(sensitiveDetail, error.ToString());
    }

    [Fact]
    public void TlsFailuresExplainHowToConfigureTrustWithoutLeakingDetails()
    {
        string sensitiveDetail = Guid.NewGuid().ToString("N");
        var error = MilocoSessionClient.CreateAuthenticationFailure(
            new HttpRequestException(sensitiveDetail, new AuthenticationException(sensitiveDetail)));

        Assert.Contains("TLS 证书", error.Message);
        Assert.Contains("TrustedServerCertificatePath", error.Message);
        Assert.DoesNotContain(sensitiveDetail, error.ToString());
    }

    [Fact]
    public async Task RequestTimeoutExplainsHowToAdjustIt()
    {
        await using WebApplication app = CreateApplication();
        app.MapPost("/api/auth/login", async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            return Results.Json(new { code = 0, data = new { username = "admin" } });
        });
        await app.StartAsync();

        using MilocoSessionClient client = CreateClient(app, TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<MilocoAuthenticationException>(() => client.EnsureAuthenticatedAsync(CancellationToken.None));

        Assert.Contains("连接 Miloco 超时", error.Message);
        Assert.Contains("RequestTimeout", error.Message);
    }

    [Fact]
    public void PinsTheConfiguredMilocoCertificateForHttpAndWebSocketValidation()
    {
        using ECDsa key = ECDsa.Create();
        CertificateRequest request = new("CN=miloco", key, HashAlgorithmName.SHA256);
        using X509Certificate2 expected = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using ECDsa otherKey = ECDsa.Create();
        CertificateRequest otherRequest = new("CN=other", otherKey, HashAlgorithmName.SHA256);
        using X509Certificate2 other = otherRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        string certificatePath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(certificatePath, expected.ExportCertificatePem());
            using MilocoSessionClient client = new(new MiCameraServerOptions
            {
                Miloco = new MilocoOptions
                {
                    BaseUrl = "https://miloco.invalid",
                    Username = "admin",
                    Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                    TrustedServerCertificatePath = certificatePath
                }
            }, NullLogger<MilocoSessionClient>.Instance);

            Assert.True(client.ValidateServerCertificate(expected, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(client.ValidateServerCertificate(other, SslPolicyErrors.None));
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    private static WebApplication CreateApplication()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }

    private static MilocoSessionClient CreateClient(WebApplication app, TimeSpan? requestTimeout = null)
    {
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new MilocoSessionClient(new MiCameraServerOptions
        {
            Miloco = new MilocoOptions
            {
                BaseUrl = address,
                Username = "admin",
                Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15)
            }
        }, NullLogger<MilocoSessionClient>.Instance);
    }
}
