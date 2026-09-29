using MiCamera.Net.RTSP.Abstractions.Media;

namespace MiCamera.Net.Media.Runtime;

internal sealed record ProcessResult(VideoSnapshot? Snapshot, IReadOnlyList<VideoAccessUnit> H264AccessUnits);
