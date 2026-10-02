using MiCamera.Net.Sample.Server.Configuration;
using MiCamera.Net;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Extensions;
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
        throw new InvalidOperationException("Usage: MiCamera.Net.Sample.Server <path-to-MiCameraConfig.json>");
    }

    configPath = args[0];
#endif
    SampleServerConfiguration configuration = SampleServerConfiguration.Load(configPath);

    using IHost host = MiCameraEngineFactory
        .CreateServerBuilder()
        .Initialize(configuration.Server)
        .WithRtsp(options => ApplyRtspOptions(options, configuration.MediaServer))
        .Build();

    await host.RunAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"MiCamera.Net could not start: {exception.Message}");
    Environment.ExitCode = 1;
}

static void ApplyRtspOptions(MiCameraRtspOptions target, MiCameraRtspOptions source)
{
    target.Rtsp = source.Rtsp;
    target.Http = source.Http;
    target.WebRtc = source.WebRtc;
    target.Media = source.Media;
    target.Snapshot = source.Snapshot;
}
