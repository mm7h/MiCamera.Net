namespace MiCamera.Net.Media.Runtime;

/// <summary>Deployment diagnostics live in the media layer, not the upstream stream receiver.</summary>
public static class MediaRuntimeDiagnostics
{
    public static MediaRuntimeReport Current => FFmpegRuntime.Report;
}
