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
}
