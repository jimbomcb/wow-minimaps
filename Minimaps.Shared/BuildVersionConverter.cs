using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minimaps.Shared;

/// <summary>
/// System.Text.Json BuildVersion serialization
/// Always transfer as string wrapped int64. Javascript int64 handling is depressing.
/// </summary>
public class BuildVersionConverter : JsonConverter<BuildVersion>
{
    public override BuildVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var versionIntString = reader.GetString();
            if (long.TryParse(versionIntString, out var versionLong))
            {
                return new BuildVersion(versionLong);
            }
            else
            {
                throw new JsonException($"Invalid BuildVersion string: {versionIntString}");
            }
        }
        else if (reader.TokenType == JsonTokenType.PropertyName)
        {
            var versionIntString = reader.GetString();
            if (long.TryParse(versionIntString, out var versionLong))
            {
                return new BuildVersion(versionLong);
            }
            else
            {
                throw new JsonException($"Invalid BuildVersion string: {versionIntString}");
            }
        }

        throw new JsonException($"Unexpected type for BuildVersion: {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, BuildVersion value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.EncodedValue.ToString());
    }

    public override BuildVersion ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return Read(ref reader, typeToConvert, options);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, [DisallowNull] BuildVersion value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.EncodedValue.ToString());
    }
}