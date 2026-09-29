using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Abstractions.Streams;
using MiCamera.Net.Server.Management;
using MiCamera.Net.Server.Protocol.Miloco;
using MiCamera.Net.Server.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MiCamera.Net.Server;

internal static class ServerBuilderExtensions
{
    public static IHostBuilder RegisterMiCameraCore(
        this IHostBuilder hostBuilder,
        MiCameraServerOptions options)
    {
        return hostBuilder.ConfigureServices((_, services) =>
        {
            services.AddSingleton<MilocoSessionClient>();
            services.AddSingleton<MilocoWebSocketClientFactory>();
            services.AddSingleton<CameraStreamHub>();
            services.AddSingleton<ICameraStreamProvider>(static provider =>
                provider.GetRequiredService<CameraStreamHub>());
            services.AddHostedService<CameraStreamSupervisor>();
        });
    }
}
