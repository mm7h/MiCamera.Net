namespace MiCamera.Net.RTSP.Services;

public sealed class SettingsActivationException(Exception innerException)
    : Exception("配置已保存，但应用配置失败，请重试应用。", innerException);
