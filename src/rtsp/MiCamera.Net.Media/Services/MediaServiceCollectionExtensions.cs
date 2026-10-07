using MiCamera.Net.RTSP.Abstractions.Media;
using Microsoft.Extensions.DependencyInjection;

namespace MiCamera.Net.Media.Services;

public static class MediaServiceCollectionExtensions
{
    public static IServiceCollection AddMiCameraMedia(this IServiceCollection services)
    {
        services.AddSingleton<MediaStreamCoordinator>();
        services.AddSingleton<INormalizedVideoStreamProvider>(static provider =>
            provider.GetRequiredService<MediaStreamCoordinator>());
        services.AddSingleton<IVideoSnapshotProvider>(static provider =>
            provider.GetRequiredService<MediaStreamCoordinator>());
        services.AddSingleton<IMediaCapabilityProvider>(static provider =>
            provider.GetRequiredService<MediaStreamCoordinator>());
        services.AddHostedService(static provider => provider.GetRequiredService<MediaStreamCoordinator>());
        return services;
    }
}
