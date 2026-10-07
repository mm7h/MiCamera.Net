using MiCamera.Net.Abstractions;
using MiCamera.Net.Server;
using Microsoft.Extensions.Hosting;

namespace MiCamera.Net;

/// <summary>
/// Entry point for creating a MiCamera hosted service.
/// </summary>
public static class MiCameraEngineFactory
{
    public static IMiCameraServerBuilder CreateServerBuilder()
    {
        return ServerBuilder.Create();
    }

    public static IMiCameraServerBuilder AsMiCameraServerBuilder(this IHostBuilder hostBuilder)
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);
        return ServerBuilder.Create(hostBuilder);
    }
}
