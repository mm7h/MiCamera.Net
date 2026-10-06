using MiCamera.Net.Server.Management;

namespace MiCamera.Net.Server;

/// <summary>Replaces camera workers after the host has loaded a new configuration.</summary>
public sealed class CameraRuntime
{
    private readonly CameraStreamSupervisor _supervisor;

    internal CameraRuntime(CameraStreamSupervisor supervisor) => this._supervisor = supervisor;

    public Task SuspendAsync() => this._supervisor.SuspendAsync();
    public Task ResumeAsync() => this._supervisor.ResumeAsync();
}
