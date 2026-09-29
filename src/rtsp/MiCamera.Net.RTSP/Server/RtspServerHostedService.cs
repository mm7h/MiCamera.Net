using System.Net;
using System.Net.Sockets;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MiCamera.Net.RTSP.Server;

internal sealed class RtspServerHostedService : BackgroundService
{
    private readonly INormalizedVideoStreamProvider _media;
    private readonly MiCameraRtspOptions _options;
    private readonly ILogger<RtspServerHostedService> _logger;
    private TcpListener? _listener;

    public RtspServerHostedService(
        INormalizedVideoStreamProvider media,
        MiCameraRtspOptions options,
        ILogger<RtspServerHostedService> logger)
    {
        this._media = media;
        this._options = options;
        this._logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(this._options.Rtsp.ListenAddress, out IPAddress? address))
        {
            throw new InvalidOperationException("Rtsp.ListenAddress must be an IP address.");
        }

        Rtsp.RtspUtils.RegisterUri();
        this._listener = new TcpListener(address, this._options.Rtsp.Port);
        this._listener.Start();
        this._logger.LogInformation("RTSP server listening on {Address}:{Port}.", address, this._options.Rtsp.Port);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (this._listener is null)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await this._listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                _ = this.HandleClientAsync(client, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                this._logger.LogError(exception, "RTSP listener failed while accepting a client.");
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        this._listener?.Stop();
        return base.StopAsync(cancellationToken);
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        try
        {
            await using RtspClientConnection connection = new(client, this._media, this._options, this._logger);
            await connection.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            this._logger.LogDebug(exception, "RTSP client session ended with an error.");
        }
    }
}
