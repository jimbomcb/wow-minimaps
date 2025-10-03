using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

/// <summary>
/// Content hashes sent over the wire as lowercase ascii bytes
/// </summary>
public class ContentHashConverter : JsonConverter<ContentHash>
{
    public override ContentHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected string token for ContentHash");

        var hashString = reader.GetString();
        if (string.IsNullOrEmpty(hashString))
            throw new JsonException("ContentHash string cannot be null or empty");

        try
        {
            return new ContentHash(hashString);
        }
        catch (Exception ex)
        {
            throw new JsonException($"Invalid ContentHash string: {hashString}", ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, ContentHash value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToHex());
    }
}