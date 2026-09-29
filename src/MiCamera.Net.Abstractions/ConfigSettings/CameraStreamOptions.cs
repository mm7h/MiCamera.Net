using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.Abstractions.ConfigSettings;

/// <summary>
/// A single Miloco DID and channel exposed by this host.
/// </summary>
public sealed class CameraStreamOptions
{
    public string StreamId { get; set; } = string.Empty;

    public string CameraDeviceId { get; set; } = string.Empty;

    public int Channel { get; set; }

    public VideoCodec Codec { get; set; } = VideoCodec.H265;

    /// <summary>
    /// Fallback rate used when the elementary stream does not expose timing information.
    /// </summary>
    public double NominalFrameRate { get; set; } = 25d;
}
