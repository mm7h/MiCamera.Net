using System.Net.WebSockets;
using MiCamera.Net.Abstractions.ConfigSettings;
using Microsoft.Extensions.Logging;
using Websocket.Client;

namespace MiCamera.Net.Server.Protocol.Miloco;

/// <summary>
/// Creates short-lived Websocket.Client instances that share the authenticated Miloco cookie container.
/// </summary>
internal sealed class MilocoWebSocketClientFactory
{
    private readonly MilocoSessionClient _session;
    private readonly MiCameraServerOptions _options;
    private readonly ILogger<WebsocketClient> _logger;

    public MilocoWebSocketClientFactory(
        MilocoSessionClient session,
        MiCameraServerOptions options,
        ILogger<WebsocketClient> logger)
    {
        this._session = session;
        this._options = options;
        this._logger = logger;
    }

    public WebsocketClient Create(Uri streamUri)
    {
        ArgumentNullException.ThrowIfNull(streamUri);

        return new WebsocketClient(streamUri, this._logger, this.CreateNativeClient)
        {
            Name = string.Concat("miloco-", streamUri.Query),
            ConnectTimeout = this._options.Streaming.ConnectTimeout,
            IsReconnectionEnabled = false,
            ReconnectTimeout = null,
            ErrorReconnectTimeout = null,
            LostReconnectTimeout = null
        };
    }

    private ClientWebSocket CreateNativeClient()
    {
        ClientWebSocket socket = new();
        socket.Options.Cookies = this._session.Cookies;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.CollectHttpResponseDetails = true;

        if (this._session.AllowInvalidServerCertificate || this._session.HasTrustedServerCertificate)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                this._session.ValidateServerCertificate(certificate, errors);
        }

        return socket;
    }
}
