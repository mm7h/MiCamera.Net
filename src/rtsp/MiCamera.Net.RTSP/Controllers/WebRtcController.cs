using MiCamera.Net.RTSP.Abstractions.Web;
using MiCamera.Net.RTSP.Services;
using Microsoft.AspNetCore.Mvc;

namespace MiCamera.Net.RTSP.Controllers;

[ApiController]
[Route("api/webrtc/sessions")]
public sealed class WebRtcController : ControllerBase
{
    private readonly WebRtcSessionService _service;

    public WebRtcController(WebRtcSessionService service)
    {
        this._service = service;
    }

    [HttpPost]
    public Task<IActionResult> Create(
        [FromBody] CreateWebRtcSessionRequest request,
        CancellationToken cancellationToken) => this._service.CreateAsync(request, cancellationToken);

    [HttpPost("{sessionId}/answer")]
    public IActionResult SetAnswer(string sessionId, [FromBody] WebRtcSessionDescriptionDto answer) =>
        this._service.SetAnswer(sessionId, answer);

    [HttpPost("{sessionId}/ice-candidates")]
    public IActionResult AddIceCandidate(string sessionId, [FromBody] WebRtcIceCandidateDto candidate) =>
        this._service.AddIceCandidate(sessionId, candidate);

    [HttpDelete("{sessionId}")]
    public IActionResult Delete(string sessionId) => this._service.Delete(sessionId);
}
