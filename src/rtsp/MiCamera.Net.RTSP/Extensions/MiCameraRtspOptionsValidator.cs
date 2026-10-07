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

        if (options.WebRtc.BindAddress is not null && !IPAddress.TryParse(options.WebRtc.BindAddress, out _))
        {
            throw new ArgumentException("WebRtc.BindAddress 必须是有效的 IP 地址。", nameof(options));
        }

        if (options.WebRtc.PortRangeStart.HasValue != options.WebRtc.PortRangeEnd.HasValue ||
            options.WebRtc.PortRangeStart is < 1 or > 65534 || options.WebRtc.PortRangeEnd is < 1 or > 65535 ||
            options.WebRtc.PortRangeStart > options.WebRtc.PortRangeEnd || options.WebRtc.PortRangeStart % 2 == 1)
        {
            throw new ArgumentException("WebRtc 端口范围必须同时指定起止端口，起始端口必须为偶数，且范围须在 1–65535 之间。", nameof(options));
        }

        if (!IPAddress.TryParse(options.Rtsp.ListenAddress, out IPAddress? rtspAddress))
        {
            throw new ArgumentException("Rtsp.ListenAddress 必须是有效的 IP 地址。", nameof(options));
        }

        if (options.Rtsp.Port is < 1 or > 65535 || options.Rtsp.RtpMtu < 300 || options.Rtsp.SessionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Rtsp.Port、Rtsp.RtpMtu 或 Rtsp.SessionTimeout 配置无效。", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Rtsp.PathPrefix) || !options.Rtsp.PathPrefix.StartsWith('/'))
        {
            throw new ArgumentException("Rtsp.PathPrefix 必须以“/”开头。", nameof(options));
        }

        bool webSetup = options.Rtsp.WebManaged;
        if (string.IsNullOrWhiteSpace(options.Rtsp.Username) != string.IsNullOrWhiteSpace(options.Rtsp.Password))
        {
            throw new ArgumentException("Rtsp.Username 和 Rtsp.Password 必须同时配置。", nameof(options));
        }

        if (!Uri.TryCreate(options.Http.ListenUrl, UriKind.Absolute, out Uri? httpUri) ||
            (httpUri.Scheme != Uri.UriSchemeHttp && httpUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Http.ListenUrl 必须是完整的 HTTP 或 HTTPS 地址。", nameof(options));
        }

        if (options.WebRtc.MaxPeersPerStream < 1 || options.Snapshot.JpegQuality is < 1 or > 100 ||
            options.Media.H264Bitrate <= 0 || options.WebRtc.IceGatheringTimeout <= TimeSpan.Zero ||
            options.WebRtc.PendingSessionTimeout <= TimeSpan.Zero || options.WebRtc.DisconnectedGracePeriod <= TimeSpan.Zero ||
            options.WebRtc.PlayoutDelay < TimeSpan.Zero ||
            options.WebRtc.TranscoderIdleTimeout < TimeSpan.Zero || options.Media.KeyFrameInterval <= TimeSpan.Zero ||
            options.Snapshot.Interval < TimeSpan.Zero)
        {
            throw new ArgumentException("一个或多个 RTSP 服务时间参数或媒体配置无效。", nameof(options));
        }

        if (options.Media.H264MaxWidth == 0 != (options.Media.H264MaxHeight == 0) ||
            options.Media.H264MaxWidth < 0 || options.Media.H264MaxHeight < 0 ||
            options.Media.H264MaxWidth % 2 == 1 || options.Media.H264MaxHeight % 2 == 1 ||
            options.Media.H264MaxWidth is > 0 and < 64 || options.Media.H264MaxHeight is > 0 and < 64)
        {
            throw new ArgumentException(
                "Media.H264MaxWidth 和 Media.H264MaxHeight 必须同时为 0（使用源分辨率），或同时为不小于 64 的偶数。",
                nameof(options));
        }

        bool httpLoopback = string.Equals(httpUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(httpUri.Host, out IPAddress? httpAddress) && IPAddress.IsLoopback(httpAddress));
        if ((!webSetup && !IPAddress.IsLoopback(rtspAddress) && (string.IsNullOrWhiteSpace(options.Rtsp.Username) || string.IsNullOrWhiteSpace(options.Rtsp.Password))) ||
            (!httpLoopback && string.IsNullOrWhiteSpace(options.Http.BearerToken)))
        {
            throw new ArgumentException(
                "RTSP 监听非回环地址时必须配置摘要认证凭据，HTTP 监听非回环地址时必须配置 Bearer 令牌。",
                nameof(options));
        }
    }
}
