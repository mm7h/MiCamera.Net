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
            throw new ArgumentException("Miloco.BaseUrl must be an absolute HTTP or HTTPS URL.", nameof(options));
        }

        if (options.Initialization.Configured && string.IsNullOrWhiteSpace(options.Miloco.Username))
        {
            throw new ArgumentException("Miloco.Username is required.", nameof(options));
        }

        if (options.Initialization.Configured && string.IsNullOrWhiteSpace(options.Miloco.Password))
        {
            throw new ArgumentException("Miloco.Password is required.", nameof(options));
        }

        EnsurePositive(options.Miloco.RequestTimeout, "Miloco.RequestTimeout");
        EnsurePositive(options.Streaming.ConnectTimeout, "Streaming.ConnectTimeout");
        EnsurePositive(options.Streaming.FirstKeyFrameTimeout, "Streaming.FirstKeyFrameTimeout");
        EnsurePositive(options.Streaming.IdleTimeout, "Streaming.IdleTimeout");

        if (options.Streaming.MaxMessageBytes <= 0)
        {
            throw new ArgumentException("Streaming.MaxMessageBytes must be greater than zero.", nameof(options));
        }

        if (options.Streaming.SubscriberBufferCapacity < 4)
        {
            throw new ArgumentException(
                "Streaming.SubscriberBufferCapacity must be at least four to deliver codec parameters and a key frame.",
                nameof(options));
        }

        EnsurePositive(options.Reconnect.InitialDelay, "Reconnect.InitialDelay");
        EnsurePositive(options.Reconnect.MaximumDelay, "Reconnect.MaximumDelay");

        if (options.Reconnect.MaximumDelay < options.Reconnect.InitialDelay)
        {
            throw new ArgumentException("Reconnect.MaximumDelay cannot be less than Reconnect.InitialDelay.", nameof(options));
        }

        if (options.Reconnect.BackoffMultiplier < 1d)
        {
            throw new ArgumentException("Reconnect.BackoffMultiplier must be at least one.", nameof(options));
        }

        if (options.Reconnect.JitterRatio is < 0d or > 1d)
        {
            throw new ArgumentException("Reconnect.JitterRatio must be between zero and one.", nameof(options));
        }

        if (options.Initialization.Configured && options.Streams.Count == 0)
        {
            throw new ArgumentException("At least one camera stream must be configured.", nameof(options));
        }

        HashSet<string> streamIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> cameraChannels = new(StringComparer.Ordinal);

        foreach (CameraStreamOptions stream in options.Streams)
        {
            if (stream is null)
            {
                throw new ArgumentException("Camera stream entries cannot be null.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(stream.StreamId))
            {
                throw new ArgumentException("Each camera stream needs a StreamId.", nameof(options));
            }

            if (!streamIds.Add(stream.StreamId))
            {
                throw new ArgumentException($"The StreamId '{stream.StreamId}' is duplicated.", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(stream.CameraDeviceId))
            {
                throw new ArgumentException($"Camera stream '{stream.StreamId}' needs a CameraDeviceId.", nameof(options));
            }

            if (stream.Channel < 0)
            {
                throw new ArgumentException($"Camera stream '{stream.StreamId}' has an invalid Channel.", nameof(options));
            }

            if (!Enum.IsDefined(stream.Codec))
            {
                throw new ArgumentException($"Camera stream '{stream.StreamId}' has an invalid Codec.", nameof(options));
            }

            if (stream.NominalFrameRate is < 1d or > 120d)
            {
                throw new ArgumentException(
                    $"Camera stream '{stream.StreamId}' NominalFrameRate must be between 1 and 120.",
                    nameof(options));
            }

            string cameraChannel = string.Concat(stream.CameraDeviceId, "\u001f", stream.Channel);
            if (!cameraChannels.Add(cameraChannel))
            {
                throw new ArgumentException(
                    $"Camera '{stream.CameraDeviceId}' channel {stream.Channel} is configured more than once.",
                    nameof(options));
            }
        }
    }

    private static void EnsurePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} must be greater than zero.");
        }
    }
}
