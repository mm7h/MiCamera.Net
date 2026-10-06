using MiCamera.Net;
using MiCamera.Net.RTSP.Extensions;
using MiCamera.Net.Sample.Server.Configuration;
using Microsoft.Extensions.Hosting;

try
{
    string configPath;
#if DEBUG
    // Debug runs fall back to the configuration next to the application so local debugging
    // needs no command-line argument.
    configPath = Path.Combine(AppContext.BaseDirectory, "Configs", "MiCameraConfig.json");
#else
    if (args.Length != 1)
    {
        throw new InvalidOperationException("用法：MiCamera.Net.Sample.Server <MiCameraConfig.json 文件路径>");
    }

    configPath = args[0];
#endif
    SampleServerConfiguration configuration = SampleServerConfiguration.Load(configPath);

    using IHost host = MiCameraEngineFactory
        .CreateServerBuilder()
        .Initialize(configuration.Server)
        // 注册媒体处理、RTSP 服务、HTTP API、Swagger、WebRTC、截图等运行服务
        .WithRtsp(options =>
        {
            options.Rtsp = configuration.MediaServer.Rtsp;
            options.Http = configuration.MediaServer.Http;
            options.WebRtc = configuration.MediaServer.WebRtc;
            options.Media = configuration.MediaServer.Media;
            options.Snapshot = configuration.MediaServer.Snapshot;
        })
        // 注册网页配置功能及 SQLite 持久化存储，用户可以通过网页配置 Miloco、摄像头流和 RTSP 凭据
        // 不能单独配置，需要搭配 WithRtsp 使用
        .WithWebSetup()
        .Build();

    await host.RunAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"MiCamera.Net 启动失败：{exception.Message}");
    Environment.ExitCode = 1;
}
