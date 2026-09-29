using System.Security.Cryptography;
using System.Text;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MiCamera.Net.RTSP.Security;

internal sealed class BearerTokenAuthorizationFilter : IAuthorizationFilter
{
    private readonly MiCameraRtspOptions _options;

    public BearerTokenAuthorizationFilter(MiCameraRtspOptions options)
    {
        this._options = options;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (string.IsNullOrWhiteSpace(this._options.Http.BearerToken))
        {
            return;
        }

        string? header = context.HttpContext.Request.Headers.Authorization;
        const string Prefix = "Bearer ";
        if (header is null || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        byte[] supplied = Encoding.UTF8.GetBytes(header[Prefix.Length..]);
        byte[] expected = Encoding.UTF8.GetBytes(this._options.Http.BearerToken);
        if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            context.Result = new UnauthorizedResult();
        }
    }
}
