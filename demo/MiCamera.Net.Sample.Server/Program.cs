// See https://aka.ms/new-console-template for more information
using System.Text.Json;
using System.Text.Json.Serialization;
using MiCamera.Net;
using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Extensions;
using Microsoft.Extensions.Hosting;

try
{
    MiCameraServerOptions options = LoadOptions();

    using IHost host = MiCameraEngineFactory
        .CreateServerBuilder()
        .Initialize(options)
        .WithRtsp(ConfigureRtsp)
        .Build();

    await host.RunAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"MiCamera.Net could not start: {exception.Message}");
    Environment.ExitCode = 1;
}

static MiCameraServerOptions LoadOptions()
{
    string configPath = Path.Combine(AppContext.BaseDirectory, "Configs", "MiCameraConfig.json");
    string json = File.ReadAllText(configPath);
    JsonSerializerOptions serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
    serializerOptions.Converters.Add(new JsonStringEnumConverter());
    serializerOptions.Converters.Add(new SecondsTimeSpanJsonConverter());

    MiCameraServerOptions options = JsonSerializer.Deserialize<MiCameraServerOptions>(json, serializerOptions)
        ?? throw new InvalidOperationException("The MiCamera configuration file is empty or invalid.");

    options.Miloco.BaseUrl = ReadEnvironment("MILOCO_BASE_URL", options.Miloco.BaseUrl);
    options.Miloco.Username = ReadEnvironment("MILOCO_USERNAME", options.Miloco.Username);
    options.Miloco.Password = ReadEnvironment("MILOCO_PASSWORD", options.Miloco.Password);

    if (options.Streams.Count == 1)
    {
        CameraStreamOptions stream = options.Streams[0];
        stream.CameraDeviceId = ReadEnvironment("CAMERA_ID", stream.CameraDeviceId);

        if (int.TryParse(Environment.GetEnvironmentVariable("STREAM_CHANNEL"), out int channel))
        {
            stream.Channel = channel;
        }

        string? codec = Environment.GetEnvironmentVariable("VIDEO_CODEC");
        if (!string.IsNullOrWhiteSpace(codec))
        {
            stream.Codec = ParseCodec(codec);
        }
    }

    return options;
}

static string ReadEnvironment(string name, string fallback)
{
    string? value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? fallback : value;
}

static void ConfigureRtsp(MiCameraRtspOptions options)
{
    options.Rtsp.ListenAddress = ReadEnvironment("RTSP_LISTEN_ADDRESS", options.Rtsp.ListenAddress);
    options.Http.ListenUrl = ReadEnvironment("RTSP_HTTP_LISTEN_URL", options.Http.ListenUrl);
    options.Http.BearerToken = ReadEnvironment("RTSP_API_TOKEN", options.Http.BearerToken);
    options.Rtsp.Username = ReadEnvironment("RTSP_USERNAME", options.Rtsp.Username);
    options.Rtsp.Password = ReadEnvironment("RTSP_PASSWORD", options.Rtsp.Password);
    options.Media.NativeLibraryPath = ReadEnvironment("FFMPEG_ROOT_PATH", options.Media.NativeLibraryPath);
    options.Http.AllowedOrigins.Clear();
    options.Http.AllowedOrigins.AddRange(ReadAllowedOrigins());

    if (int.TryParse(Environment.GetEnvironmentVariable("RTSP_PORT"), out int port))
    {
        options.Rtsp.Port = port;
    }
}

static IReadOnlyList<string> ReadAllowedOrigins()
{
    const string DefaultPreviewOrigin = "http://127.0.0.1:5081";
    string configuredOrigins = ReadEnvironment("RTSP_ALLOWED_ORIGINS", DefaultPreviewOrigin);

    return configuredOrigins
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(NormalizeOrigin)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static string NormalizeOrigin(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
        || !string.IsNullOrEmpty(uri.Query)
        || !string.IsNullOrEmpty(uri.Fragment)
        || uri.AbsolutePath != "/")
    {
        throw new InvalidOperationException(
            "RTSP_ALLOWED_ORIGINS must contain semicolon-separated HTTP or HTTPS origins without a path.");
    }

    return uri.GetLeftPart(UriPartial.Authority);
}

static VideoCodec ParseCodec(string value)
{
    return value.Trim().ToLowerInvariant() switch
    {
        "h264" => VideoCodec.H264,
        "h265" or "hevc" => VideoCodec.H265,
        _ => throw new InvalidOperationException($"VIDEO_CODEC '{value}' must be h264, h265, or hevc.")
    };
}

sealed class SecondsTimeSpanJsonConverter : JsonConverter<TimeSpan>
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
