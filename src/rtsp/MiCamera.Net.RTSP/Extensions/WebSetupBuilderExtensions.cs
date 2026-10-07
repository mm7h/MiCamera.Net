using MiCamera.Net.Abstractions;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Services;
using MiCamera.Net.Server.Protocol.Miloco;
using Microsoft.Extensions.DependencyInjection;

namespace MiCamera.Net.RTSP.Extensions;

public static class WebSetupBuilderExtensions
{
    public static IMiCameraServerBuilder WithWebSetup(this IMiCameraServerBuilder builder, string dataDirectory = "./data")
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("数据目录不能为空或仅包含空白字符。", nameof(dataDirectory));
        }
        builder.HostBuilder.ConfigureServices((_, services) =>
        {
            services.AddSingleton(provider =>
            {
                MiCameraServerOptions server = provider.GetRequiredService<MiCameraServerOptions>();
                MiCameraRtspOptions media = provider.GetRequiredService<MiCameraRtspOptions>();
                if (!server.Initialization.WebManaged || !media.Rtsp.WebManaged)
                {
                    throw new InvalidOperationException("使用网页配置前，必须在构建器初始化之前启用 Server.Initialization.WebManaged 和 Rtsp.WebManaged。");
                }
                return new ApplicationSettingsStore(dataDirectory, server, media);
            });
            services.AddSingleton<MilocoConfigurationClient>();
            services.AddSingleton<SetupSettingsService>();
        });
        return builder;
    }
}
