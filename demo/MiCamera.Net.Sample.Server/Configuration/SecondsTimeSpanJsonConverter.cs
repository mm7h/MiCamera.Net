using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiCamera.Net.Sample.Server.Configuration;

internal sealed class SecondsTimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out double seconds))
        {
            throw new JsonException("时间间隔必须使用 JSON 数字表示，单位为秒。");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.TotalSeconds);
    }
}
