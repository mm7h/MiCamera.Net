using FFmpeg.AutoGen;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;

namespace MiCamera.Net.Media.Runtime;

internal static class FFmpegRuntime
{
    private static int s_configured;
    private static bool s_isAvailable;
    private static Exception? s_failure;

    public static bool IsAvailable => Volatile.Read(ref s_configured) == 1 && s_isAvailable;

    public static Exception? Failure => s_failure;

    public static void Configure(MediaProcessingOptions options)
    {
        if (Interlocked.Exchange(ref s_configured, 1) == 1)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(options.NativeLibraryPath))
            {
                ffmpeg.RootPath = options.NativeLibraryPath;
            }

            _ = ffmpeg.av_version_info();
            s_isAvailable = true;
        }
        catch (Exception exception)
        {
            s_failure = exception;
            s_isAvailable = false;
        }
    }
}
