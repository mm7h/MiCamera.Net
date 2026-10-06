using System.Text.Json;
using MiCamera.Net.Abstractions.ConfigSettings;
using Microsoft.Extensions.Logging;

namespace MiCamera.Net.Server.Protocol.Miloco;

public sealed record MilocoCameraDevice(string Did, string Name, string? RoomName, bool Online, int? ChannelCount);

/// <summary>Validates candidate credentials without altering the active streaming session.</summary>
public sealed class MilocoConfigurationClient(ILoggerFactory loggerFactory)
{
    public async Task<IReadOnlyList<MilocoCameraDevice>> DiscoverAsync(MilocoOptions options, CancellationToken cancellationToken)
    {
        using MilocoSessionClient session = new(new MiCameraServerOptions { Miloco = options },
            loggerFactory.CreateLogger<MilocoSessionClient>());
        try
        {
            JsonElement data = await session.GetCamerasAsync(cancellationToken).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Array)
                throw new MilocoAuthenticationException("Miloco 摄像头列表格式不兼容，请检查服务版本。");
            List<MilocoCameraDevice> devices = [];
            foreach (JsonElement item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("did", out JsonElement did) ||
                    did.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(did.GetString()))
                    throw new MilocoAuthenticationException("Miloco 摄像头列表缺少有效的 DID，请检查服务版本。");
                string? Text(string key) => item.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                int? channels = item.TryGetProperty("channel_count", out JsonElement count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out int number) && number > 0
                    ? number : null;
                devices.Add(new(did.GetString()!, Text("name") ?? "未命名摄像头", Text("room_name"),
                    item.TryGetProperty("online", out JsonElement online) && online.ValueKind == JsonValueKind.True, channels));
            }
            return devices;
        }
        catch (MilocoAuthenticationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw MilocoSessionClient.CreateAuthenticationFailure(exception); }
    }
}
