using MiCamera.Net.Abstractions.ConfigSettings;

namespace MiCamera.Net.Server.Management;

internal static class MiCameraOptionsValidator
{
    public static void Validate(MiCameraServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Miloco);
        ArgumentNullException.ThrowIfNull(options.Streaming);
        ArgumentNullException.ThrowIfNull(options.Reconnect);
        ArgumentNullException.ThrowIfNull(options.Streams);

        if (options.Initialization.Configured && (!Uri.TryCreate(options.Miloco.BaseUrl, UriKind.Absolute, out Uri? baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new ArgumentException("Miloco.BaseUrl 必须是完整的 HTTP 或 HTTPS 地址。", nameof(options));
        }

        if (options.Initialization.Configured && string.IsNullOrWhiteSpace(options.Miloco.Username))
        {
            throw new ArgumentException("必须配置 Miloco.Username。", nameof(options));
        }

        if (options.Initialization.Configured && string.IsNullOrWhiteSpace(options.Miloco.Password))
        {
            throw new ArgumentException("必须配置 Miloco.Password。", nameof(options));
        }

        EnsurePositive(options.Miloco.RequestTimeout, "Miloco.RequestTimeout");
        EnsurePositive(options.Streaming.ConnectTimeout, "Streaming.ConnectTimeout");
        EnsurePositive(options.Streaming.FirstKeyFrameTimeout, "Streaming.FirstKeyFrameTimeout");
        EnsurePositive(options.Streaming.IdleTimeout, "Streaming.IdleTimeout");

        if (options.Streaming.MaxMessageBytes <= 0)
        {
            throw new ArgumentException("Streaming.MaxMessageBytes 必须大于 0。", nameof(options));
        }

        if (options.Streaming.SubscriberBufferCapacity < 4)
        {
            throw new ArgumentException(
                "Streaming.SubscriberBufferCapacity 必须至少为 4，才能传递编码参数和关键帧。",
                nameof(options));
        }

        EnsurePositive(options.Reconnect.InitialDelay, "Reconnect.InitialDelay");
        EnsurePositive(options.Reconnect.MaximumDelay, "Reconnect.MaximumDelay");

        if (options.Reconnect.MaximumDelay < options.Reconnect.InitialDelay)
        {
            throw new ArgumentException("Reconnect.MaximumDelay 不能小于 Reconnect.InitialDelay。", nameof(options));
        }

        if (options.Reconnect.BackoffMultiplier < 1d)
        {
            throw new ArgumentException("Reconnect.BackoffMultiplier 必须至少为 1。", nameof(options));
        }

        if (options.Reconnect.JitterRatio is < 0d or > 1d)
        {
            throw new ArgumentException("Reconnect.JitterRatio 必须在 0–1 之间。", nameof(options));
        }

        if (options.Initialization.Configured && options.Streams.Count == 0)
        {
            throw new ArgumentException("必须至少配置一路摄像头流。", nameof(options));
        }

        HashSet<string> streamIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> cameraChannels = new(StringComparer.Ordinal);

        foreach (CameraStreamOptions stream in options.Streams)
        {
            if (stream is null)
            {
                throw new ArgumentException("摄像头流配置项不能为 null。", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(stream.StreamId))
            {
                throw new ArgumentException("每路摄像头流都必须配置 StreamId。", nameof(options));
            }

            if (!streamIds.Add(stream.StreamId))
            {
                throw new ArgumentException($"StreamId“{stream.StreamId}”重复。", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(stream.CameraDeviceId))
            {
                throw new ArgumentException($"摄像头流“{stream.StreamId}”必须配置 CameraDeviceId。", nameof(options));
            }

            if (stream.Channel < 0)
            {
                throw new ArgumentException($"摄像头流“{stream.StreamId}”的 Channel 配置无效。", nameof(options));
            }

            if (!Enum.IsDefined(stream.Codec))
            {
                throw new ArgumentException($"摄像头流“{stream.StreamId}”的 Codec 配置无效。", nameof(options));
            }

            if (stream.NominalFrameRate is < 1d or > 120d)
            {
                throw new ArgumentException(
                    $"摄像头流“{stream.StreamId}”的 NominalFrameRate 必须在 1–120 之间。",
                    nameof(options));
            }

            string cameraChannel = string.Concat(stream.CameraDeviceId, "\u001f", stream.Channel);
            if (!cameraChannels.Add(cameraChannel))
            {
                throw new ArgumentException(
                    $"摄像头“{stream.CameraDeviceId}”的通道 {stream.Channel} 被重复配置。",
                    nameof(options));
            }
        }
    }

    private static void EnsurePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} 必须大于 0。");
        }
    }
}
