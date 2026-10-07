namespace MiCamera.Net.RTSP.Abstractions.Web;

public sealed record WebRtcIceCandidateDto(
    string Candidate,
    string? SdpMid,
    ushort? SdpMLineIndex,
    string? UsernameFragment);
