namespace MiCamera.Net.Server.Common;

public sealed record MilocoCameraDevice(string Did, string Name, string? RoomName, bool Online, int? ChannelCount);
