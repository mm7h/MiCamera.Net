using MiCamera.Net.Abstractions.Common.Models;

namespace MiCamera.Net.Abstractions.Streams;

/// <summary>
/// Provides live, decodable Miloco video streams to downstream integrations such as RTSP.
/// </summary>
public interface ICameraStreamProvider
{
    IReadOnlyCollection<CameraStreamDescriptor> Streams { get; }

    bool TryGetSnapshot(string streamId, out CameraStreamSnapshot? snapshot);

    IAsyncEnumerable<EncodedVideoChunk> SubscribeAsync(
        string streamId,
        CancellationToken cancellationToken = default);
}
