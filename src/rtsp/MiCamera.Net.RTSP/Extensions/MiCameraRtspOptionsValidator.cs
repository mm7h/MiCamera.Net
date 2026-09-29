using System.Net;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;

namespace MiCamera.Net.RTSP.Extensions;

internal static class MiCameraRtspOptionsValidator
{
    public static void Validate(MiCameraRtspOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Rtsp);
        ArgumentNullException.ThrowIfNull(options.Http);
        ArgumentNullException.ThrowIfNull(options.WebRtc);
        ArgumentNullException.ThrowIfNull(options.Media);
        ArgumentNullException.ThrowIfNull(options.Snapshot);

        if (!IPAddress.TryParse(options.Rtsp.ListenAddress, out IPAddress? rtspAddress))
        {
            throw new ArgumentException("Rtsp.ListenAddress must be an IP address.", nameof(options));
        }

        if (options.Rtsp.Port is < 1 or > 65535 || options.Rtsp.RtpMtu < 300 || options.Rtsp.SessionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Rtsp.Port, Rtsp.RtpMtu, or Rtsp.SessionTimeout is invalid.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Rtsp.PathPrefix) || !options.Rtsp.PathPrefix.StartsWith('/'))
        {
            throw new ArgumentException("Rtsp.PathPrefix must start with '/'.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Rtsp.Username) != string.IsNullOrWhiteSpace(options.Rtsp.Password))
        {
            throw new ArgumentException("Rtsp.Username and Rtsp.Password must be configured together.", nameof(options));
        }

        if (!Uri.TryCreate(options.Http.ListenUrl, UriKind.Absolute, out Uri? httpUri) ||
            (httpUri.Scheme != Uri.UriSchemeHttp && httpUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Http.ListenUrl must be an absolute HTTP or HTTPS URL.", nameof(options));
        }

        if (options.WebRtc.MaxPeersPerStream < 1 || options.Snapshot.JpegQuality is < 1 or > 100 ||
            options.Media.H264Bitrate <= 0 || options.WebRtc.IceGatheringTimeout <= TimeSpan.Zero ||
            options.WebRtc.PendingSessionTimeout <= TimeSpan.Zero || options.WebRtc.DisconnectedGracePeriod <= TimeSpan.Zero ||
            options.WebRtc.TranscoderIdleTimeout < TimeSpan.Zero || options.Media.KeyFrameInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("One or more RTSP service timing or media options are invalid.", nameof(options));
        }

        bool httpLoopback = string.Equals(httpUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(httpUri.Host, out IPAddress? httpAddress) && IPAddress.IsLoopback(httpAddress));
        if ((!IPAddress.IsLoopback(rtspAddress) && (string.IsNullOrWhiteSpace(options.Rtsp.Username) || string.IsNullOrWhiteSpace(options.Rtsp.Password))) ||
            (!httpLoopback && string.IsNullOrWhiteSpace(options.Http.BearerToken)))
        {
            throw new ArgumentException(
                "Non-loopback RTSP requires Digest credentials and non-loopback HTTP requires a Bearer token.",
                nameof(options));
        }
    }
}
