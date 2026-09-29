namespace MiCamera.Net.RTSP.Abstractions.Media;

public interface IVideoSnapshotProvider
{
    bool TryGetSnapshot(string streamId, out VideoSnapshot? snapshot);
}
