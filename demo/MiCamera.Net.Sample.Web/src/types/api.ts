export interface CameraStreamInfo {
    streamId: string;
    cameraId: string;
    channel: number;
    sourceCodec: string;
    state: string;
    lastReceivedAt: string | null;
    rtspUrl: string;
    snapshotAvailable: boolean;
    webRtcAvailable: boolean;
}

export interface SessionDescription {
    type: RTCSdpType;
    sdp: string;
}

export interface WebRtcSessionOffer {
    sessionId: string;
    offer: SessionDescription;
    expiresAt: string;
}

export interface IceCandidate {
    candidate: string;
    sdpMid: string | null;
    sdpMLineIndex: number | null;
    usernameFragment: string | null;
}
