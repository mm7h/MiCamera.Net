using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.RTSP.Abstractions.Media;

public interface IMediaCapabilityProvider
{
    bool CanProvide(string streamId, VideoCodec codec, out string? reason);
}
