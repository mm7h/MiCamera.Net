using System.Net;
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

        this._client = new FlurlClientBuilder(this.BaseUri.AbsoluteUri.TrimEnd('/'))
            .ConfigureInnerHandler(handler =>
            {
                handler.UseCookies = true;
                handler.CookieContainer = this.Cookies;
                handler.AllowAutoRedirect = false;

                if (options.Miloco.AllowInvalidServerCertificate)
                {
                    handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
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
    }

    public Uri BaseUri { get; }

    public CookieContainer Cookies { get; }

    public bool AllowInvalidServerCertificate => this._options.Miloco.AllowInvalidServerCertificate;

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
                throw new MilocoAuthenticationException(await CreateHttpFailureMessageAsync(
                    "Miloco login failed",
                    loginResponse).ConfigureAwait(false));
            }

            using IFlurlResponse statusResponse = await this._client
                .Request("api", "miot", "login_status")
                .AllowAnyHttpStatus()
                .WithTimeout(this._options.Miloco.RequestTimeout)
                .GetAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!statusResponse.ResponseMessage.IsSuccessStatusCode)
            {
                throw new MilocoAuthenticationException(await CreateHttpFailureMessageAsync(
                    "Miloco login status check failed",
                    statusResponse).ConfigureAwait(false));
            }

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
            throw new MilocoAuthenticationException("Miloco login request failed.", exception);
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
        this._authenticationLock.Dispose();
    }

    private static string CombinePath(string prefix, string suffix)
    {
        string normalizedPrefix = prefix.Trim('/');
        return string.IsNullOrEmpty(normalizedPrefix)
            ? string.Concat('/', suffix)
            : string.Concat('/', normalizedPrefix, '/', suffix);
    }

    private static async Task<string> CreateHttpFailureMessageAsync(string prefix, IFlurlResponse response)
    {
        string body;
        try
        {
            body = await response.GetStringAsync().ConfigureAwait(false);
        }
        catch
        {
            body = string.Empty;
        }

        const int MaxBodyLength = 512;
        if (body.Length > MaxBodyLength)
        {
            body = string.Concat(body.AsSpan(0, MaxBodyLength), "…");
        }

        return string.IsNullOrWhiteSpace(body)
            ? string.Concat(prefix, " (HTTP ", response.StatusCode, ").")
            : string.Concat(prefix, " (HTTP ", response.StatusCode, "): ", body);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);
    }
}
