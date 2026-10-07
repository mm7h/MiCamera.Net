using MiCamera.Net.Abstractions.ConfigSettings;
using Microsoft.Extensions.Hosting;

namespace MiCamera.Net.Abstractions;

/// <summary>
/// Builds a hosted Xiaomi camera streaming service.
/// </summary>
public interface IMiCameraServerBuilder
{
    /// <summary>
    /// The underlying generic host builder. Extension packages, including the future RTSP package,
    /// use this entry point to register their services.
    /// </summary>
    IHostBuilder HostBuilder { get; }

    /// <summary>
    /// Registers the Miloco connection and stream configuration.
    /// </summary>
    IMiCameraServerBuilder Initialize(MiCameraServerOptions options);

    /// <summary>
    /// Builds the service host. Network connections begin when the returned host is started.
    /// </summary>
    IHost Build();
}
