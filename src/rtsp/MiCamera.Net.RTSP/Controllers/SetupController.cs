using MiCamera.Net.RTSP.Services;
using MiCamera.Net.Server.Protocol.Miloco;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace MiCamera.Net.RTSP.Controllers;

[ApiController]
public sealed class SetupController(SetupSettingsService settings) : ControllerBase
{
    [HttpGet("api/setup")]
    public ActionResult<SetupStatus> Status() => settings.Status();

    [HttpGet("api/settings")]
    public ActionResult<SettingsView> Settings() => settings.Settings();

    [HttpPost("api/setup/discover")]
    [ProducesResponseType(typeof(IReadOnlyList<MilocoCameraDevice>), 200)]
    [Consumes("application/json")]
    public Task<IActionResult> Discover(DiscoverSettingsRequest request, CancellationToken cancellationToken) =>
        this.RunAsync(async () => this.Ok(await settings.DiscoverAsync(request, cancellationToken)));

    [HttpPut("api/settings")]
    [ProducesResponseType(typeof(SetupStatus), 200)]
    [Consumes("application/json")]
    public Task<IActionResult> Save(SaveSettingsRequest request, CancellationToken cancellationToken) =>
        this.RunAsync(async () => this.Ok(await settings.SaveAsync(request, cancellationToken)));

    [HttpPost("api/setup/activate")]
    [ProducesResponseType(typeof(SetupStatus), 200)]
    public Task<IActionResult> Activate(CancellationToken cancellationToken) =>
        this.RunAsync(async () => this.Ok(await settings.ActivateAsync(cancellationToken)));

    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (SettingsConflictException exception) { return this.Conflict(new { error = exception.Message }); }
        catch (ArgumentException exception) { return this.BadRequest(new { error = exception.Message }); }
        catch (MilocoAuthenticationException exception) { return this.StatusCode(502, new { error = exception.Message }); }
        catch (SettingsActivationException exception) { return this.StatusCode(503, new { error = exception.Message }); }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        { return this.StatusCode(503, new { error = "无法保存配置或启用 RTSP，请检查数据目录权限、磁盘空间和端口占用后重试。" }); }
    }
}
