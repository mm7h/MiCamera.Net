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
            throw new JsonException("The MiCamera configuration must be a JSON object.");
        }

        // Reject the old layout instead of silently ignoring credentials or listener settings.
        string[] legacySections = ["Rtsp", "Http", "Media", "WebRtc", "Snapshot"];
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "Streams", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException("Top-level Streams is no longer supported. Move it to MediaServer.Rtsp.Streams.");
            }

            if (legacySections.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new JsonException($"Top-level {property.Name} is no longer supported. Move media service settings into MediaServer.");
            }
        }

        MiCameraServerOptions serverOptions = document.RootElement.Deserialize<MiCameraServerOptions>(serializerOptions)!;
        ConfigurationFile configuration = document.RootElement.Deserialize<ConfigurationFile>(serializerOptions)!;
        if (configuration.MediaServer is null)
        {
            throw new JsonException("MediaServer must be a JSON object.");
        }

        JsonElement mediaServer = GetProperty(document.RootElement, "MediaServer");
        JsonElement rtsp = GetProperty(mediaServer, "Rtsp");
        JsonElement streams = GetProperty(rtsp, "Streams");
        // The JSON layout groups streams under RTSP, but the core also supplies WebRTC and snapshots.
        serverOptions.Streams = streams.ValueKind == JsonValueKind.Undefined
            ? []
            : streams.Deserialize<List<CameraStreamOptions>>(serializerOptions)
                ?? throw new JsonException("MediaServer.Rtsp.Streams must be a JSON array.");

        return new SampleServerConfiguration(serverOptions, configuration.MediaServer.ToRuntimeOptions());
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        // Match the serializer's case-insensitive names and last-property-wins behavior.
        return element.EnumerateObject()
            .LastOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private sealed class ConfigurationFile
    {
        [JsonRequired]
        public MediaServerConfiguration? MediaServer { get; set; }
    }

    private sealed class MediaServerConfiguration
    {
        public string ListenAddress { get; set; } = "http://127.0.0.1:5080";
        public string BearerToken { get; set; } = string.Empty;
        public List<string> AllowedOrigins { get; set; } = [];
        public FFmpegConfiguration FFmpeg { get; set; } = new();
        public RtspEndpointOptions Rtsp { get; set; } = new();
        public WebRtcOptions WebRtc { get; set; } = new();
        public SnapshotOptions Snapshot { get; set; } = new();

        public MiCameraRtspOptions ToRuntimeOptions()
        {
            if (FFmpeg is null)
            {
                throw new JsonException("MediaServer.FFmpeg must be a JSON object.");
            }

            return new MiCameraRtspOptions
            {
                Http = new HttpEndpointOptions
                {
                    ListenUrl = ListenAddress,
                    BearerToken = BearerToken,
                    AllowedOrigins = AllowedOrigins
                },
                Media = new MediaProcessingOptions
                {
                    NativeLibraryPath = string.IsNullOrWhiteSpace(FFmpeg.Path) || System.IO.Path.IsPathFullyQualified(FFmpeg.Path)
                        ? FFmpeg.Path
                        : System.IO.Path.GetFullPath(FFmpeg.Path, AppContext.BaseDirectory),
                    H264EncoderName = FFmpeg.H264EncoderName,
                    H264Bitrate = FFmpeg.H264Bitrate,
                    H264Preset = FFmpeg.H264Preset,
                    H264MaxWidth = FFmpeg.H264MaxWidth,
                    H264MaxHeight = FFmpeg.H264MaxHeight,
                    KeyFrameInterval = FFmpeg.KeyFrameInterval
                },
                Rtsp = Rtsp,
                WebRtc = WebRtc,
                Snapshot = Snapshot
            };
        }
    }

    private sealed class FFmpegConfiguration
    {
        public string Path { get; set; } = string.Empty;
        public string H264EncoderName { get; set; } = "libx264";
        public int H264Bitrate { get; set; } = 2_500_000;
        public string H264Preset { get; set; } = "veryfast";
        public int H264MaxWidth { get; set; }
        public int H264MaxHeight { get; set; }
        public TimeSpan KeyFrameInterval { get; set; } = TimeSpan.FromSeconds(2);
    }

    private sealed class SecondsTimeSpanJsonConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out double seconds))
            {
                throw new JsonException("Durations must be JSON numbers expressed in seconds.");
            }

            return TimeSpan.FromSeconds(seconds);
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value.TotalSeconds);
        }
    }
}
