using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Flurl.Http;
using Flurl.Http.Configuration;
using MiCamera.Net.Abstractions.ConfigSettings;
using Microsoft.Extensions.Logging;

namespace MiCamera.Net.Server.Protocol.Miloco;

/// <summary>
/// Owns the shared HTTP session and authentication cookie used by all Miloco camera connections.
/// </summary>
internal sealed class MilocoSessionClient : IDisposable
{
    private readonly MiCameraServerOptions _options;
    private readonly ILogger<MilocoSessionClient> _logger;
    private readonly SemaphoreSlim _authenticationLock = new(1, 1);
    private readonly IFlurlClient _client;
    private readonly X509Certificate2? _trustedServerCertificate;
    private volatile bool _isAuthenticated;
    private bool _disposed;

    public MilocoSessionClient(
        MiCameraServerOptions options,
        ILogger<MilocoSessionClient> logger)
    {
        this._options = options;
        this._logger = logger;
        this.BaseUri = new Uri(options.Miloco.BaseUrl, UriKind.Absolute);
        this.Cookies = new CookieContainer();
        this._trustedServerCertificate = LoadTrustedServerCertificate(options.Miloco.TrustedServerCertificatePath);

        this._client = new FlurlClientBuilder(this.BaseUri.AbsoluteUri.TrimEnd('/'))
            .ConfigureInnerHandler(handler =>
            {
                handler.UseCookies = true;
                handler.CookieContainer = this.Cookies;
                handler.AllowAutoRedirect = false;

                if (options.Miloco.AllowInvalidServerCertificate || this._trustedServerCertificate is not null)
                {
                    handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                        this.ValidateServerCertificate(certificate, errors);
                }
            })
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .Build();

        if (options.Miloco.AllowInvalidServerCertificate)
        {
            this._logger.LogWarning(
                "Miloco server certificate validation is disabled for {MilocoBaseUrl}.",
                this.BaseUri.GetLeftPart(UriPartial.Authority));
        }
        else if (this._trustedServerCertificate is not null)
        {
            this._logger.LogInformation(
                "Miloco server certificate is pinned for {MilocoBaseUrl}.",
                this.BaseUri.GetLeftPart(UriPartial.Authority));
        }
    }

    public Uri BaseUri { get; }

    public CookieContainer Cookies { get; }

    public bool AllowInvalidServerCertificate => this._options.Miloco.AllowInvalidServerCertificate;

    public bool HasTrustedServerCertificate => this._trustedServerCertificate is not null;

    public bool ValidateServerCertificate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (this._options.Miloco.AllowInvalidServerCertificate)
        {
            return true;
        }

        if (this._trustedServerCertificate is null)
        {
            return errors == SslPolicyErrors.None;
        }

        return certificate is not null && CryptographicOperations.FixedTimeEquals(
            certificate.GetRawCertData(), this._trustedServerCertificate.RawData);
    }

    public async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        this.ThrowIfDisposed();
        await this._authenticationLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this._isAuthenticated)
            {
                return;
            }

            using IFlurlResponse loginResponse = await this._client
                .Request("api", "auth", "login")
                .AllowAnyHttpStatus()
                .WithTimeout(this._options.Miloco.RequestTimeout)
                .PostJsonAsync(
                    new
                    {
                        username = this._options.Miloco.Username,
                        password = this._options.Miloco.Password
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!loginResponse.ResponseMessage.IsSuccessStatusCode)
            {
                throw CreateLocalLoginFailure(loginResponse.StatusCode);
            }

            _ = MilocoResponseValidator.ReadData(await loginResponse.GetStringAsync().ConfigureAwait(false));

            using IFlurlResponse statusResponse = await this._client
                .Request("api", "miot", "login_status")
                .AllowAnyHttpStatus()
                .WithTimeout(this._options.Miloco.RequestTimeout)
                .GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!statusResponse.ResponseMessage.IsSuccessStatusCode)
            {
                throw new MilocoAuthenticationException(
                    $"Miloco 小米账号登录状态检查失败（HTTP {statusResponse.StatusCode}）。请重新登录 Miloco 本地服务，并确认小米账号已在 Miloco 网页完成绑定。");
            }

            MilocoResponseValidator.EnsureXiaomiAccountAuthorized(await statusResponse.GetStringAsync().ConfigureAwait(false));

            this._isAuthenticated = true;
            this._logger.LogInformation("Authenticated with Miloco at {MilocoBaseUrl}.", this.BaseUri.GetLeftPart(UriPartial.Authority));
        }
        catch (MilocoAuthenticationException)
        {
            this._isAuthenticated = false;
            throw;
        }
        catch (OperationCanceledException)
        {
            this._isAuthenticated = false;
            throw;
        }
        catch (Exception exception)
        {
            this._isAuthenticated = false;
            // Third-party exception text can contain response bodies or authentication material.
            throw CreateAuthenticationFailure(exception);
        }
        finally
        {
            this._authenticationLock.Release();
        }
    }

    public void InvalidateAuthentication()
    {
        this._isAuthenticated = false;
    }

    public Uri BuildVideoStreamUri(string cameraId, int channel)
    {
        UriBuilder builder = new(this.BaseUri)
        {
            Scheme = this.BaseUri.Scheme == Uri.UriSchemeHttps ? Uri.UriSchemeWss : Uri.UriSchemeWs,
            Port = this.BaseUri.IsDefaultPort ? -1 : this.BaseUri.Port,
            Path = CombinePath(this.BaseUri.AbsolutePath, "api/miot/ws/video_stream"),
            Query = string.Concat(
                "camera_id=", Uri.EscapeDataString(cameraId),
                "&channel=", channel.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };

        return builder.Uri;
    }

    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        this._client.Dispose();
        this._trustedServerCertificate?.Dispose();
        this._authenticationLock.Dispose();
    }

    /// <summary>
    /// Converts transport failures into actionable messages without exposing upstream response content.
    /// </summary>
    internal static MilocoAuthenticationException CreateAuthenticationFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        SocketError? socketError = FindSocketError(exception);
        if (socketError is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
        {
            return new MilocoAuthenticationException(
                "无法解析 Miloco 主机名。请检查 Miloco.BaseUrl 或 MILOCO_BASE_URL；从宿主机直接启动时，应填写 Miloco 的局域网 IP，不能使用仅容器网络可见的服务名。");
        }

        if (socketError == SocketError.ConnectionRefused)
        {
            return new MilocoAuthenticationException(
                "Miloco 拒绝了连接。请确认服务已启动，并检查 Miloco.BaseUrl 或 MILOCO_BASE_URL 中的地址和端口（默认端口为 8000）。");
        }

        if (ContainsException<AuthenticationException>(exception))
        {
            return new MilocoAuthenticationException(
                "无法验证 Miloco 的 TLS 证书。开发环境的自签名证书可临时启用 AllowInvalidServerCertificate；生产环境请配置 TrustedServerCertificatePath 或 MILOCO_SERVER_CERTIFICATE_PATH。");
        }

        if (ContainsException<FlurlHttpTimeoutException>(exception) || socketError == SocketError.TimedOut)
        {
            return new MilocoAuthenticationException(
                "连接 Miloco 超时。请检查服务状态、网络、防火墙以及 Miloco.BaseUrl 或 MILOCO_BASE_URL；必要时可增大 RequestTimeout。");
        }

        if (ContainsException<HttpRequestException>(exception) || socketError is not null)
        {
            return new MilocoAuthenticationException(
                "无法连接 Miloco。请检查 Miloco.BaseUrl 或 MILOCO_BASE_URL、服务状态、网络和防火墙。");
        }

        return new MilocoAuthenticationException(
            "Miloco 登录请求未能完成。请检查 Miloco.BaseUrl 或 MILOCO_BASE_URL、TLS 设置和 Miloco 版本兼容性；为保护认证信息，未输出底层响应内容。");
    }

    private static string CombinePath(string prefix, string suffix)
    {
        string normalizedPrefix = prefix.Trim('/');
        return string.IsNullOrEmpty(normalizedPrefix)
            ? string.Concat('/', suffix)
            : string.Concat('/', normalizedPrefix, '/', suffix);
    }

    private static X509Certificate2? LoadTrustedServerCertificate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return X509Certificate2.CreateFromPem(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Miloco trusted server certificate could not be loaded.", exception);
        }
    }

    private static SocketError? FindSocketError(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException)
            {
                return socketException.SocketErrorCode;
            }
        }

        return null;
    }

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }

    private static MilocoAuthenticationException CreateLocalLoginFailure(int statusCode) =>
        statusCode is 401 or 403
            ? new MilocoAuthenticationException(
                $"Miloco 本地登录被拒绝（HTTP {statusCode}）。请检查用户名和本地密码；密码应为小写 MD5 值，不是小米账号密码。")
            : new MilocoAuthenticationException(
                $"Miloco 本地登录失败（HTTP {statusCode}）。请检查 Miloco 服务状态，以及当前版本是否与本项目兼容。响应内容未输出，以保护认证信息。");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
    }
}
