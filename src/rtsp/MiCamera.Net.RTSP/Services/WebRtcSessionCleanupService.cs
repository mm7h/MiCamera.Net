using Microsoft.Extensions.Hosting;

namespace MiCamera.Net.RTSP.Services;

internal sealed class WebRtcSessionCleanupService(WebRtcSessionService sessions) : BackgroundService
{
    private readonly WebRtcSessionService _sessions = sessions;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            this._sessions.RemoveExpiredSessions();
        }
    }
}
