using FFmpeg.AutoGen;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;

namespace MiCamera.Net.Media.Runtime;

internal static class FFmpegRuntime
{
    private static int s_configured;
    private static bool s_isAvailable;
    private static readonly object s_sync = new();

    public static MediaRuntimeReport Report { get; private set; } = new(false, false, false, false, false, "原生运行时尚未初始化。");

    public static bool IsAvailable => Volatile.Read(ref s_configured) == 1 && s_isAvailable;

    public static Exception? Failure { get; private set; }

    public static void Configure(MediaProcessingOptions options)
    {
        lock (s_sync)
        {
            if (s_configured == 1)
            {
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(options.FFmpegLibPath))
                {
                    ffmpeg.RootPath = options.FFmpegLibPath;
                }

                _ = ffmpeg.av_version_info();
                // Compare against constants in the locked bindings rather than a CLI version string.
                if ((ffmpeg.avcodec_version() >> 16) != ffmpeg.LIBAVCODEC_VERSION_MAJOR ||
                    (ffmpeg.avutil_version() >> 16) != ffmpeg.LIBAVUTIL_VERSION_MAJOR ||
                    (ffmpeg.swscale_version() >> 16) != ffmpeg.LIBSWSCALE_VERSION_MAJOR)
                {
                    throw new InvalidOperationException("FFmpeg 原生库 ABI 与锁定版本的 FFmpeg.AutoGen 绑定不匹配。");
                }

                unsafe
                {
                    Report = new(true,
                        ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264) != null,
                        ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC) != null,
                        ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG) != null,
                        ffmpeg.avcodec_find_encoder_by_name(options.H264EncoderName) != null,
                        null);
                }

                s_isAvailable = true;
            }
            catch (Exception exception)
            {
                Failure = exception;
                s_isAvailable = false;
                Report = new(false, false, false, false, false, $"原生运行时不可用（{exception.GetType().Name}）。");
            }

            Volatile.Write(ref s_configured, 1);
        }
    }
}
