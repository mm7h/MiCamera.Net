namespace MiCamera.Net.Abstractions.ConfigSettings;

/// <summary>
/// Authentication and transport settings for a Miloco instance.
/// </summary>
public sealed class MilocoOptions
{
    public string BaseUrl { get; set; } = "https://miloco:8000";

    public string Username { get; set; } = "admin";

    /// <summary>
    /// The lower-case MD5 password expected by the local micam Miloco contract.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Enables self-signed Miloco certificates. This is disabled by default.
    /// </summary>
    public bool AllowInvalidServerCertificate { get; set; }

    /// <summary>
    /// Optional PEM certificate to pin for the Miloco HTTPS and WebSocket endpoints.
    /// When supplied, only the exact certificate in this file is accepted.
    /// </summary>
    public string? TrustedServerCertificatePath { get; set; }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
