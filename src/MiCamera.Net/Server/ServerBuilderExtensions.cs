using MiCamera.Net.Abstractions.Streams;
using MiCamera.Net.Server.Management;
using MiCamera.Net.Server.Protocol.Miloco;
using MiCamera.Net.Server.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MiCamera.Net.Server;

internal static class ServerBuilderExtensions
{
    public static IHostBuilder RegisterMiCameraCore(this IHostBuilder hostBuilder)
    {
        return hostBuilder.ConfigureServices((_, services) =>
        {
            services.AddSingleton<MilocoSessionClient>();
            services.AddSingleton<MilocoWebSocketClientFactory>();
            services.AddSingleton<CameraStreamHub>();
            services.AddSingleton<ICameraStreamProvider>(static provider =>
                provider.GetRequiredService<CameraStreamHub>());
            services.AddSingleton<CameraStreamSupervisor>();
            services.AddHostedService(provider => provider.GetRequiredService<CameraStreamSupervisor>());
            services.AddSingleton(provider => new CameraRuntime(provider.GetRequiredService<CameraStreamSupervisor>()));
        });
    }
}
