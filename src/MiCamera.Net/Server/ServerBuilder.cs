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
            throw new InvalidOperationException("The MiCamera server builder has already built a host.");
        }

        if (this._initialized)
        {
            throw new InvalidOperationException("The MiCamera server builder can only be initialized once.");
        }

        MiCameraOptionsValidator.Validate(options);
        this._initialized = true;

        this.HostBuilder
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(options);
            })
            .RegisterMiCameraCore(options);

        return this;
    }

    public IHost Build()
    {
        if (!this._initialized)
        {
            throw new InvalidOperationException("Initialize must be called before Build.");
        }

        if (this._built)
        {
            throw new InvalidOperationException("The MiCamera server builder can only build one host.");
        }

        this._built = true;
        return this.HostBuilder.Build();
    }
}
