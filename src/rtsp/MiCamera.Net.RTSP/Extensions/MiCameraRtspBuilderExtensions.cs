using System.Net;
using MiCamera.Net.Abstractions;
using MiCamera.Net.Media.Services;
using MiCamera.Net.RTSP.Abstractions.ConfigSettings;
using MiCamera.Net.RTSP.Controllers;
using MiCamera.Net.RTSP.Security;
using MiCamera.Net.RTSP.Server;
using MiCamera.Net.RTSP.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MiCamera.Net.RTSP.Extensions;

/// <summary>
/// Adds the optional RTSP, snapshot, and WebRTC services to a MiCamera host.
/// </summary>
public static class MiCameraRtspBuilderExtensions
{
    private const string RegistrationKey = "MiCamera.Net.RTSP.Registered";
    private const string CorsPolicyName = "MiCamera.Net.RTSP.Cors";

    public static IMiCameraServerBuilder WithRtsp(
        this IMiCameraServerBuilder builder,
        Action<MiCameraRtspOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.HostBuilder.Properties.ContainsKey(RegistrationKey))
        {
            throw new InvalidOperationException("WithRtsp() can only be called once for a server builder.");
        }

        MiCameraRtspOptions options = new();
        configure?.Invoke(options);
        MiCameraRtspOptionsValidator.Validate(options);
        builder.HostBuilder.Properties[RegistrationKey] = true;

        builder.HostBuilder.ConfigureServices((_, services) =>
        {
            services.AddSingleton(options);
            services.AddMiCameraMedia();
            services.AddSingleton<CameraApiService>();
            services.AddSingleton<WebRtcSessionService>();
            services.AddHostedService<RtspServerHostedService>();
            services.AddHostedService<WebRtcSessionCleanupService>();
            services.AddSingleton<BearerTokenAuthorizationFilter>();
            services.AddControllers(mvc => mvc.Filters.AddService<BearerTokenAuthorizationFilter>())
                .AddApplicationPart(typeof(CamerasController).Assembly);
            services.AddCors(cors => cors.AddPolicy(CorsPolicyName, policy =>
            {
                if (options.Http.AllowedOrigins.Count > 0)
                {
                    policy.WithOrigins(options.Http.AllowedOrigins.ToArray())
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                }
            }));
        });

        builder.HostBuilder.ConfigureWebHostDefaults(webBuilder =>
        {
            webBuilder.UseUrls(options.Http.ListenUrl);
            webBuilder.Configure(app =>
            {
                app.UseRouting();
                if (options.Http.AllowedOrigins.Count > 0)
                {
                    app.UseCors(CorsPolicyName);
                }

                app.UseEndpoints(endpoints => endpoints.MapControllers());
            });
        });

        return builder;
    }
}
