using FFmpeg.AutoGen;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;

namespace MiCamera.Net.Media.Runtime;

internal static class FFmpegRuntime
{
    private static int s_configured;
    private static bool s_isAvailable;
    private static Exception? s_failure;
    private static readonly object s_sync = new();

    public static MediaRuntimeReport Report { get; private set; } = new(false, false, false, false, false, "Native runtime has not been initialized.");

    public static bool IsAvailable => Volatile.Read(ref s_configured) == 1 && s_isAvailable;

    public static Exception? Failure => s_failure;

    public static void Configure(MediaProcessingOptions options)
    {
        lock (s_sync)
        {
            if (s_configured == 1) return;

            try
            {
                if (!string.IsNullOrWhiteSpace(options.NativeLibraryPath))
                {
                    ffmpeg.RootPath = options.NativeLibraryPath;
                }

                _ = ffmpeg.av_version_info();
                // Compare against constants in the locked bindings rather than a CLI version string.
                if ((ffmpeg.avcodec_version() >> 16) != ffmpeg.LIBAVCODEC_VERSION_MAJOR ||
                    (ffmpeg.avutil_version() >> 16) != ffmpeg.LIBAVUTIL_VERSION_MAJOR ||
                    (ffmpeg.swscale_version() >> 16) != ffmpeg.LIBSWSCALE_VERSION_MAJOR)
                {
                    throw new InvalidOperationException("FFmpeg native ABI does not match the locked FFmpeg.AutoGen bindings.");
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
                s_failure = exception;
                s_isAvailable = false;
                Report = new(false, false, false, false, false, $"Native runtime unavailable ({exception.GetType().Name}).");
            }

            Volatile.Write(ref s_configured, 1);
        }
    }
}
