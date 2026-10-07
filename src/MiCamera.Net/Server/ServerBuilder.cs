using MiCamera.Net.Abstractions;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Server.Management;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MiCamera.Net.Server;

internal sealed class ServerBuilder : IMiCameraServerBuilder
{
    private bool _initialized;
    private bool _built;

    private ServerBuilder(IHostBuilder hostBuilder)
    {
        this.HostBuilder = hostBuilder;
    }

    public IHostBuilder HostBuilder { get; }

    internal static IMiCameraServerBuilder Create()
    {
        return new ServerBuilder(Host.CreateDefaultBuilder());
    }

    internal static IMiCameraServerBuilder Create(IHostBuilder hostBuilder)
    {
        return new ServerBuilder(hostBuilder);
    }

    public IMiCameraServerBuilder Initialize(MiCameraServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (this._built)
        {
            throw new InvalidOperationException("MiCamera 服务构建器已创建宿主。");
        }

        if (this._initialized)
        {
            throw new InvalidOperationException("MiCamera 服务构建器只能初始化一次。");
        }

        MiCameraOptionsValidator.Validate(options);
        this._initialized = true;

        this.HostBuilder
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(options);
            })
            .RegisterMiCameraCore();

        return this;
    }

    public IHost Build()
    {
        if (!this._initialized)
        {
            throw new InvalidOperationException("调用 Build 之前必须先调用 Initialize。");
        }

        if (this._built)
        {
            throw new InvalidOperationException("MiCamera 服务构建器只能创建一个宿主。");
        }

        this._built = true;
        return this.HostBuilder.Build();
    }
}
