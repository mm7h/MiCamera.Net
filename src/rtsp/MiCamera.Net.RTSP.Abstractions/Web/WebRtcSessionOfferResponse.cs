namespace MiCamera.Net.RTSP.Abstractions.Web;

public sealed record WebRtcSessionOfferResponse(
    string SessionId,
    WebRtcSessionDescriptionDto Offer,
    DateTimeOffset ExpiresAt);
