using System.Text.Json;

namespace MiCamera.Net.Server.Protocol.Miloco;

/// <summary>Validates the legacy standalone Miloco NormalResponse without echoing remote bodies.</summary>
internal static class MilocoResponseValidator
{
    public static JsonElement ReadData(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("code", out JsonElement code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int value) || value != 0 ||
                !root.TryGetProperty("data", out JsonElement data))
            {
                throw new MilocoAuthenticationException(
                    "Miloco 返回了失败或不受支持的响应。请确认 Miloco 服务已正常运行，且当前版本与本项目兼容；响应内容未输出，以保护认证信息。");
            }

            return data.Clone();
        }
        catch (JsonException)
        {
            throw new MilocoAuthenticationException(
                "Miloco 返回的响应不是有效 JSON。请确认访问的是 Miloco 本地服务，并检查版本兼容性；响应内容未输出，以保护认证信息。");
        }
    }

    public static void EnsureXiaomiAccountAuthorized(string json)
    {
        JsonElement data = ReadData(json);
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("is_logged_in", out JsonElement status) ||
            status.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new MilocoAuthenticationException(
                "Miloco 返回的小米账号登录状态格式不受支持。请确认 Miloco 版本兼容，并在其网页检查小米账号绑定状态。");
        }

        if (!status.GetBoolean())
        {
            throw new MilocoAuthenticationException("小米账号尚未授权。请在 Miloco 网页完成账号绑定后重试。");
        }
    }
}
