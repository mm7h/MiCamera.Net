namespace MiCamera.Net.RTSP.Server;

internal sealed record RtspRequest(string Method, string Uri, IReadOnlyDictionary<string, string> Headers);
