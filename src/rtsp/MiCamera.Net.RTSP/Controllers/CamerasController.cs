using MiCamera.Net.RTSP.Services;
using Microsoft.AspNetCore.Mvc;

namespace MiCamera.Net.RTSP.Controllers;

[ApiController]
[Route("api/cameras")]
public sealed class CamerasController(CameraApiService service) : ControllerBase
{
    private readonly CameraApiService _service = service;

    [HttpGet]
    public IActionResult GetCameras() => this._service.GetCameras();

    [HttpGet("{streamId}/snapshot")]
    public IActionResult GetSnapshot(string streamId) => this._service.GetSnapshot(streamId);
}
