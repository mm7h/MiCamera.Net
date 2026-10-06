using MiCamera.Net.Abstractions;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Services;
using MiCamera.Net.Server.Protocol.Miloco;
using Microsoft.Extensions.DependencyInjection;

namespace MiCamera.Net.RTSP.Extensions;

public static class WebSetupBuilderExtensions
{
    public static IMiCameraServerBuilder WithWebSetup(this IMiCameraServerBuilder builder, string? dataDirectory = null)
    {
        string directory = dataDirectory ?? Environment.GetEnvironmentVariable("MICAMERA_DATA_DIRECTORY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiCamera.Net");
        builder.HostBuilder.ConfigureServices((_, services) =>
        {
            services.AddSingleton(provider =>
            {
                MiCameraServerOptions server = provider.GetRequiredService<MiCameraServerOptions>();
                MiCameraRtspOptions media = provider.GetRequiredService<MiCameraRtspOptions>();
                if (!server.Initialization.WebManaged || !media.Rtsp.WebManaged)
                    throw new InvalidOperationException("Web setup requires Server.Initialization.WebManaged and Rtsp.WebManaged before builder initialization.");
                return new ApplicationSettingsStore(directory, server, media);
            });
            services.AddSingleton<MilocoConfigurationClient>();
            services.AddSingleton<SetupSettingsService>();
        });
        return builder;
    }
}
