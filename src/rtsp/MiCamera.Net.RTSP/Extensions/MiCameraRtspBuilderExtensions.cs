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
using Microsoft.OpenApi.Models;

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
            services.AddSingleton<RtspServerHostedService>();
            services.AddHostedService(provider => provider.GetRequiredService<RtspServerHostedService>());
            services.AddHostedService<WebRtcSessionCleanupService>();
            services.AddSingleton<BearerTokenAuthorizationFilter>();
            services.AddControllers(mvc => mvc.Filters.AddService<BearerTokenAuthorizationFilter>())
                .AddApplicationPart(typeof(CamerasController).Assembly)
                .AddJsonOptions(json => json.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen(swagger =>
            {
                swagger.SwaggerDoc("v1", new OpenApiInfo { Title = "MiCamera.Net API", Version = "v1" });
                swagger.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "Token",
                    Description = "填写 HTTP API Bearer Token；与 Miloco PIN、RTSP 密码不同。"
                });
                swagger.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
                });
                swagger.UseInlineDefinitionsForEnums();
            });
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
                app.UseSwagger();
                app.UseSwaggerUI(swagger => swagger.SwaggerEndpoint("v1/swagger.json", "MiCamera.Net v1"));
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
