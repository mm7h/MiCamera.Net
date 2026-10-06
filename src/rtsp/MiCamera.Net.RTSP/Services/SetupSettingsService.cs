using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MiCamera.Net.Abstractions.ConfigSettings;
using MiCamera.Net.Media.Services;
using MiCamera.Net.RTSP.Server;
using MiCamera.Net.Server;
using MiCamera.Net.Server.Protocol.Miloco;

namespace MiCamera.Net.RTSP.Services;

public sealed record SetupStatus(bool Configured, bool Listening, string? Username, long Version, bool ApplyPending);
public sealed record SettingsView(long Version, string MilocoBaseUrl, bool HasMilocoPin, string RtspUsername,
    bool HasRtspPassword, IReadOnlyList<CameraStreamOptions> Streams);
public sealed record DiscoverSettingsRequest(string BaseUrl, string? Pin);
public sealed record SaveSettingsRequest(long Version, string BaseUrl, string? Pin, string RtspUsername,
    string? RtspPassword, List<CameraStreamOptions> Streams);

public sealed class SetupSettingsService(ApplicationSettingsStore store, MiCameraServerOptions server,
    RtspServerHostedService rtsp, MilocoConfigurationClient miloco, CameraRuntime cameras,
    MediaStreamCoordinator media, WebRtcSessionService webRtc)
{
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SetupStatus Status()
    {
        SavedApplicationSettings? saved = store.Read();
        return new(saved is not null, rtsp.Listening, rtsp.Username, saved?.Version ?? 0,
            saved is not null && store.ActiveVersion != saved.Version);
    }

    public SettingsView Settings()
    {
        SavedApplicationSettings? saved = store.Read();
        return new(saved?.Version ?? 0, saved?.MilocoBaseUrl ?? "", saved is not null,
            saved?.RtspUsername ?? "", saved is not null, saved?.Streams ?? []);
    }

    private MilocoOptions Candidate(string baseUrl, string? pin, SavedApplicationSettings? saved)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Miloco 地址须为完整 HTTP/HTTPS 地址，不能包含账号、查询参数或片段。");
        string hash;
        if (string.IsNullOrEmpty(pin)) hash = saved?.MilocoPasswordMd5 ?? throw new ArgumentException("请输入六位 PIN 码。");
        else
        {
            if (!Regex.IsMatch(pin, "\\A[0-9]{6}\\z")) throw new ArgumentException("PIN 码须为六位数字。");
            hash = Md5(pin);
        }
        return new()
        {
            BaseUrl = uri.AbsoluteUri.TrimEnd('/'), Username = "admin", Password = hash,
            AllowInvalidServerCertificate = server.Miloco.AllowInvalidServerCertificate,
            TrustedServerCertificatePath = server.Miloco.TrustedServerCertificatePath,
            RequestTimeout = server.Miloco.RequestTimeout
        };
    }

    public Task<IReadOnlyList<MilocoCameraDevice>> DiscoverAsync(DiscoverSettingsRequest request, CancellationToken cancellationToken) =>
        miloco.DiscoverAsync(this.Candidate(request.BaseUrl, request.Pin, store.Read()), cancellationToken);

    public async Task<SetupStatus> ActivateAsync(CancellationToken cancellationToken)
    {
        await this._saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        System.Net.Sockets.TcpListener? listener = null;
        try
        {
            SavedApplicationSettings saved = store.Read() ?? throw new ArgumentException("请先完成配置。");
            if (rtsp.Listening && store.ActiveVersion == saved.Version) return this.Status();
            if (!rtsp.HasListener) listener = rtsp.ReserveListener();
            await this.ApplyAsync(saved, listener).ConfigureAwait(false);
            listener = null;
            return this.Status();
        }
        finally { listener?.Stop(); this._saveLock.Release(); }
    }

    public async Task<SetupStatus> SaveAsync(SaveSettingsRequest request, CancellationToken cancellationToken)
    {
        await this._saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        System.Net.Sockets.TcpListener? listener = null;
        try
        {
            SavedApplicationSettings? previous = store.Read();
            if ((previous?.Version ?? 0) != request.Version) throw new SettingsConflictException();
            MilocoOptions candidate = this.Candidate(request.BaseUrl, request.Pin, previous);
            string username = request.RtspUsername?.Trim() ?? "";
            string digest;
            if (string.IsNullOrEmpty(request.RtspPassword))
            {
                if (previous is null || username != previous.RtspUsername)
                    throw new ArgumentException("首次设置或修改 RTSP 用户名时必须填写密码。");
                digest = previous.RtspDigestHa1;
            }
            else
            {
                RtspServerHostedService.ValidateCredentials(username, request.RtspPassword);
                digest = Md5($"{username}:MiCamera.Net:{request.RtspPassword}");
            }
            SavedApplicationSettings proposed = new(0, candidate.BaseUrl, candidate.Password, username, digest, request.Streams);
            ValidateSaved(proposed);
            IReadOnlyList<MilocoCameraDevice> devices = await miloco.DiscoverAsync(candidate, cancellationToken).ConfigureAwait(false);
            foreach (CameraStreamOptions stream in proposed.Streams)
            {
                MilocoCameraDevice? device = devices.FirstOrDefault(device => device.Did == stream.CameraDeviceId);
                if (device is null) throw new ArgumentException($"摄像头 {stream.CameraDeviceId} 已不在 Miloco 列表中，请刷新设备。");
                if (device.ChannelCount is int count && stream.Channel >= count)
                    throw new ArgumentException($"摄像头 {device.Name} 的通道超出范围。");
            }
            if (!rtsp.HasListener) listener = rtsp.ReserveListener();
            cancellationToken.ThrowIfCancellationRequested();
            SavedApplicationSettings saved = store.Save(proposed, request.Version);
            // Finish application even if the submitting browser disconnects after the commit.
            await this.ApplyAsync(saved, listener).ConfigureAwait(false);
            listener = null;
            return this.Status();
        }
        finally
        {
            listener?.Stop();
            this._saveLock.Release();
        }
    }

    private async Task ApplyAsync(SavedApplicationSettings saved, System.Net.Sockets.TcpListener? listener)
    {
        try
        {
            await rtsp.PauseAsync().ConfigureAwait(false);
            await webRtc.PauseAsync().ConfigureAwait(false);
            if (server.Initialization.Configured)
            {
                await media.SuspendAsync().ConfigureAwait(false);
                await cameras.SuspendAsync().ConfigureAwait(false);
                store.Apply(saved);
                await cameras.ResumeAsync().ConfigureAwait(false);
                await media.ResumeAsync().ConfigureAwait(false);
            }
            else
            {
                store.Apply(saved);
                server.Initialization.Complete();
            }
            if (listener is not null) rtsp.ActivateListener(listener);
            rtsp.Resume();
            webRtc.Resume();
            store.MarkActive(saved.Version);
        }
        catch (Exception exception) { throw new SettingsActivationException(exception); }
    }

    public static void ValidateSaved(SavedApplicationSettings settings)
    {
        if (!Uri.TryCreate(settings.MilocoBaseUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") ||
            !Regex.IsMatch(settings.MilocoPasswordMd5, "\\A[0-9a-f]{32}\\z") ||
            !Regex.IsMatch(settings.RtspDigestHa1, "\\A[0-9a-f]{32}\\z")) throw new ArgumentException("持久化认证配置格式无效。");
        RtspServerHostedService.ValidateCredentials(settings.RtspUsername, "validation");
        if (settings.Streams is null || settings.Streams.Count == 0) throw new ArgumentException("至少选择一路摄像头流。");
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        HashSet<(string, int)> channels = [];
        foreach (CameraStreamOptions stream in settings.Streams)
        {
            if (stream is null || !Regex.IsMatch(stream.StreamId ?? "", "\\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\\z") ||
                !Regex.IsMatch(stream.CameraDeviceId ?? "", "\\A[A-Za-z0-9._:-]{1,128}\\z") || stream.Channel < 0 ||
                !Enum.IsDefined(stream.Codec) || !double.IsFinite(stream.NominalFrameRate) || stream.NominalFrameRate is < 1 or > 120)
                throw new ArgumentException("流配置无效，请检查名称、DID、通道、编码及帧率（1–120）。");
            if (!ids.Add(stream.StreamId!)) throw new ArgumentException("StreamId 不能重复（不区分大小写）。");
            if (!channels.Add((stream.CameraDeviceId!, stream.Channel))) throw new ArgumentException("同一摄像头通道不能重复配置。");
        }
    }

    private static string Md5(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class SettingsActivationException(Exception innerException)
    : Exception("配置已保存，但应用配置失败，请重试应用。", innerException);
