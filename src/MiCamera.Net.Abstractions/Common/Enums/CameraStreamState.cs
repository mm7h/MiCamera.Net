namespace MiCamera.Net.Abstractions.Common.Enums;

public enum CameraStreamState
{
    Stopped = 0,
    Authenticating = 1,
    Connecting = 2,
    WaitingForKeyFrame = 3,
    Streaming = 4,
    Reconnecting = 5,
    Faulted = 6
}
