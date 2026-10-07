using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Server.Common;
using Microsoft.Extensions.Logging;

namespace MiCamera.Net.Server.Protocol.Miloco;

/// <summary>Validates candidate credentials without altering the active streaming session.</summary>
public sealed class MilocoConfigurationClient(ILoggerFactory loggerFactory)
{
    public async Task<IReadOnlyList<MilocoCameraDevice>> DiscoverAsync(MilocoOptions options, CancellationToken cancellationToken)
    {
        using MilocoSessionClient session = new(new MiCameraServerOptions { Miloco = options },
            loggerFactory.CreateLogger<MilocoSessionClient>());
        try
        {
            return await session.GetCamerasAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MilocoAuthenticationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw MilocoSessionClient.CreateAuthenticationFailure(exception); }
    }
}
