using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.Sys;

namespace MiCamera.Net.RTSP.Services;

internal sealed class CameraPeerConnection(RTCConfiguration configuration, PortRange? ports,
    ILogger sessionLogger, string streamId) : RTCPeerConnection(configuration, portRange: ports)
{
    public override void Close(string reason)
    {
        if (!this.IsClosed)
        {
            sessionLogger.LogInformation("正在关闭摄像头流 {StreamId} 的 WebRTC 连接：{Reason}。", streamId, reason);
        }

        base.Close(reason);
    }
}
