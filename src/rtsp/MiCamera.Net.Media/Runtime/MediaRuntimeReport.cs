namespace MiCamera.Net.Media.Runtime;

public sealed record MediaRuntimeReport(bool NativeLibrariesAvailable, bool H264Decoder, bool HevcDecoder,
    bool MjpegEncoder, bool H264Encoder, string? Failure)
{
    public bool FullyAvailable => this.NativeLibrariesAvailable && this.H264Decoder && this.HevcDecoder && this.MjpegEncoder && this.H264Encoder;
}
