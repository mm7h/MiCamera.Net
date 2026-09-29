using Microsoft.Extensions.Hosting;

namespace MiCamera.Net.RTSP.Services;

internal sealed class WebRtcSessionCleanupService : BackgroundService
{
    private readonly WebRtcSessionService _sessions;

    public WebRtcSessionCleanupService(WebRtcSessionService sessions)
    {
        this._sessions = sessions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            this._sessions.RemoveExpiredSessions();
        }
    }
}
