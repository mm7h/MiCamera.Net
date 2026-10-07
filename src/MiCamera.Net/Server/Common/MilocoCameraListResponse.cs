namespace MiCamera.Net.Server.Common;

internal sealed record MilocoCameraListResponse(int? Code, List<MilocoCameraDevice>? Data);
