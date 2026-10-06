using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;
using MiCamera.Net.RTSP.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MiCamera.Net.RTSP.Server;

public sealed class RtspServerHostedService : BackgroundService
{
    private readonly INormalizedVideoStreamProvider _media;
    private readonly MiCameraRtspOptions _options;
    private readonly ILogger<RtspServerHostedService> _logger;
    private volatile TcpListener? _listener;
    private readonly object _listenerLock = new();
    private readonly TaskCompletionSource _listenerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;
    private volatile bool _paused;
    private readonly Dictionary<TcpClient, (CancellationTokenSource Cancellation, Task Task)> _clients = [];

    public bool Configured => !this._options.Rtsp.WebManaged || !string.IsNullOrEmpty(this._options.Rtsp.DigestHa1);
    internal bool HasListener => this._listener is not null;
    public bool Listening => this._listener is not null && this.Configured && !this._stopping && !this._paused;
    public string? Username => this.Configured ? this._options.Rtsp.Username : null;

    public RtspServerHostedService(
        INormalizedVideoStreamProvider media,
        MiCameraRtspOptions options,
        ILogger<RtspServerHostedService> logger,
        ApplicationSettingsStore? settingsStore = null)
    {
        this._media = media;
        this._options = options;
        this._logger = logger;
        // Resolving the store restores the saved options before any hosted service starts.
        _ = settingsStore;
    }

    public static void ValidateCredentials(string? username, string? password)
    {
        if (username is null || !Regex.IsMatch(username, "\\A[A-Za-z0-9._-]{1,64}\\z") ||
            string.IsNullOrWhiteSpace(password) || password.Length > 256 || password.Any(char.IsControl))
            throw new ArgumentException("用户名须为 1–64 位字母、数字、点、下划线或连字符；密码须为 1–256 个字符，不能为纯空白或包含控制字符。");
    }

    public TcpListener ReserveListener()
    {
        lock (this._listenerLock)
        {
            if (this._stopping) throw new IOException("RTSP service is stopping.");
            return this.CreateListener();
        }
    }

    public void ActivateListener(TcpListener listener)
    {
        lock (this._listenerLock)
        {
            if (this._stopping) throw new IOException("RTSP service is stopping.");
            this._listener = listener;
            this._listenerReady.TrySetResult();
        }
    }

    private TcpListener CreateListener()
    {
        TcpListener listener = new(IPAddress.Parse(this._options.Rtsp.ListenAddress), this._options.Rtsp.Port);
        listener.Start();
        return listener;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(this._options.Rtsp.ListenAddress, out IPAddress? address))
        {
            throw new InvalidOperationException("Rtsp.ListenAddress must be an IP address.");
        }

        Rtsp.RtspUtils.RegisterUri();
        if (this.Configured)
        {
            this._listener = this.CreateListener();
            this._listenerReady.TrySetResult();
            this._logger.LogInformation("RTSP server listening on {Address}:{Port}.", address, this._options.Rtsp.Port);
        }
        else this._logger.LogInformation("RTSP is closed pending web credential setup.");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await this._listenerReady.Task.WaitAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TcpClient client = await this._listener!.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                lock (this._listenerLock)
                {
                    if (this._paused || this._stopping) { client.Dispose(); continue; }
                    CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    Task task = Task.Run(async () =>
                    {
                        try { await this.HandleClientAsync(client, cancellation.Token).ConfigureAwait(false); }
                        finally
                        {
                            lock (this._listenerLock) this._clients.Remove(client);
                            cancellation.Dispose();
                            client.Dispose();
                        }
                    });
                    this._clients.Add(client, (cancellation, task));
                }
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (this._listenerLock)
        {
            this._stopping = true;
            this._listener?.Stop();
        }
        await this.PauseAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync()
    {
        KeyValuePair<TcpClient, (CancellationTokenSource Cancellation, Task Task)>[] clients;
        lock (this._listenerLock)
        {
            this._paused = true;
            clients = this._clients.ToArray();
        }
        foreach (var client in clients)
        {
            try { client.Value.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* The client finished before cancellation. */ }
            client.Key.Dispose();
        }
        await Task.WhenAll(clients.Select(client => client.Value.Task)).ConfigureAwait(false);
    }

    public void Resume()
    {
        lock (this._listenerLock)
        {
            if (this._stopping) throw new IOException("RTSP service is stopping.");
            this._paused = false;
        }
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
