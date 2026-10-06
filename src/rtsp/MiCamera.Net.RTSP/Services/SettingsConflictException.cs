namespace MiCamera.Net.RTSP.Services;

public sealed class SettingsConflictException : Exception
{
    public SettingsConflictException() : base("配置已被其他页面修改，请重新打开配置后再保存。") { }
}
