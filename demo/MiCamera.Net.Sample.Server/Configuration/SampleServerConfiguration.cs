using System.Text.Json;
using System.Text.Json.Serialization;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;

namespace MiCamera.Net.Sample.Server.Configuration;

internal sealed record SampleServerConfiguration(MiCameraServerOptions Server, MiCameraRtspOptions MediaServer)
{
    public static SampleServerConfiguration Load(string configPath)
    {
        string json = File.ReadAllText(Path.GetFullPath(configPath));
        JsonSerializerOptions serializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        serializerOptions.Converters.Add(new SecondsTimeSpanJsonConverter());

        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("MiCamera 配置必须是 JSON 对象。");
        }

        // Reject the old layout instead of silently ignoring credentials or listener settings.
        string[] legacySections = ["Rtsp", "Http", "Media", "WebRtc", "Snapshot"];
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "Streams", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException("不再支持顶层 Streams 配置，请通过网页配置摄像头流。");
            }

            if (legacySections.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new JsonException($"不再支持顶层 {property.Name} 配置，请将媒体服务配置移至 MediaServer。");
            }
        }

        MiCameraServerOptions serverOptions = document.RootElement.Deserialize<MiCameraServerOptions>(serializerOptions)!;
        serverOptions.Initialization.WebManaged = true;
        // User-managed values come exclusively from SQLite, including on upgraded deployments.
        serverOptions.Miloco.BaseUrl = string.Empty;
        serverOptions.Miloco.Username = "admin";
        serverOptions.Miloco.Password = string.Empty;
        serverOptions.Streams = [];
        ConfigurationFile configuration = document.RootElement.Deserialize<ConfigurationFile>(serializerOptions)!;
        if (configuration.MediaServer is null)
        {
            throw new JsonException("MediaServer 必须是 JSON 对象。");
        }

        MiCameraRtspOptions media = configuration.MediaServer.ToRuntimeOptions();
        media.Rtsp.Username = string.Empty;
        media.Rtsp.Password = string.Empty;
        media.Rtsp.WebManaged = true;
        return new SampleServerConfiguration(serverOptions, media);
    }
}
