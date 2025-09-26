using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minimaps.Shared;

/// <summary>
/// System.Text.Json BuildVersion serialization
/// Always transfer as string. Javascript int64 handling is depressing.
/// </summary>
public class BuildVersionConverter : JsonConverter<BuildVersion>
{
    public override BuildVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var versionString = reader.GetString();
            if (BuildVersion.TryParse(versionString!, out var version))
                return version;
            throw new JsonException($"Invalid BuildVersion format: {versionString}");
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            var value = reader.GetInt64();
            return (BuildVersion)value;
        }

        throw new JsonException($"Unexpected type for BuildVersion: {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, BuildVersion value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}