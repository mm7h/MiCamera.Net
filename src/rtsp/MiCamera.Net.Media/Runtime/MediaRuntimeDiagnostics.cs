using MiCamera.Net.Abstractions.Common.Enums;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Abstractions.Media;

namespace MiCamera.Net.Media.Runtime;

public sealed record MediaRuntimeReport(bool NativeLibrariesAvailable, bool H264Decoder, bool HevcDecoder,
    bool MjpegEncoder, bool H264Encoder, string? Failure)
{
    public bool FullyAvailable => NativeLibrariesAvailable && H264Decoder && HevcDecoder && MjpegEncoder && H264Encoder;
}

/// <summary>Deployment diagnostics live in the media layer, not the upstream stream receiver.</summary>
public static class MediaRuntimeDiagnostics
{
    public static MediaRuntimeReport Current => FFmpegRuntime.Report;

    public static void RunSelfTest(string fixtureDirectory, string nativeLibraryPath)
    {
        MediaProcessingOptions options = new() { NativeLibraryPath = nativeLibraryPath };
        FFmpegRuntime.Configure(options);
        if (!Current.FullyAvailable) throw new InvalidOperationException("Required FFmpeg native ABI or codecs are unavailable.");

        foreach (VideoCodec codec in new[] { VideoCodec.H264, VideoCodec.H265 })
        {
            byte[] data = File.ReadAllBytes(Path.Combine(fixtureDirectory, codec == VideoCodec.H264 ? "sample.h264" : "sample.h265"));
            if (!FFmpegFrameProcessor.TryCreate("self-test", codec, 1, options, 85, out FFmpegFrameProcessor? processor, out _))
                throw new InvalidOperationException($"Cannot initialize {codec} native decoder.");
            using (processor)
            {
                ProcessResult result = processor!.Process(new VideoAccessUnit("self-test", codec, data, 1, 0, 90_000, true, true), codec == VideoCodec.H265);
                if (result.Snapshot is not { Jpeg.Length: > 4 } snapshot || snapshot.Jpeg.Span[0] != 0xff || snapshot.Jpeg.Span[1] != 0xd8)
                    throw new InvalidOperationException($"{codec} native decode/JPEG self-test failed.");
                if (codec == VideoCodec.H265 && result.H264AccessUnits.Count == 0)
                    throw new InvalidOperationException("Native H.265 to H.264 self-test produced no output.");
            }
        }
    }
}
