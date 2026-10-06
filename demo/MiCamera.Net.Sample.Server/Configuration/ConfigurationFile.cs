using System.Text.Json.Serialization;

namespace MiCamera.Net.Sample.Server.Configuration;

internal sealed class ConfigurationFile
{
    [JsonRequired]
    public MediaServerConfiguration? MediaServer { get; set; }
}
