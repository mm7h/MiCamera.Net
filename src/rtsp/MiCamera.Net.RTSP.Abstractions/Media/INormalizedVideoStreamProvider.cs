using MiCamera.Net.Abstractions.Common.Enums;

namespace MiCamera.Net.RTSP.Abstractions.Media;

public interface INormalizedVideoStreamProvider
{
    IAsyncEnumerable<VideoAccessUnit> SubscribeAsync(
        string streamId,
        VideoCodec? requestedCodec = null,
        CancellationToken cancellationToken = default);

    bool TryGetCodecParameters(string streamId, out VideoCodecParameters? parameters);

    void RequestKeyFrame(string streamId);
}
