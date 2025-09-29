using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

/// <summary>
/// Formats as {"0,5": "hash", "12,34": "hash"}
/// </summary>
public class MinimapCompositionConverter : JsonConverter<MinimapComposition>
{
    public override MinimapComposition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token");

        var composition = new Dictionary<TileCoord, string>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected PropertyName");

            var coordString = reader.GetString();
            if (string.IsNullOrEmpty(coordString))
                throw new JsonException("Coordinate string cannot be null or empty");

            var parts = coordString.Split(',');
            if (parts.Length != 2)
                throw new JsonException($"Invalid coordinate format '{coordString}'. Expected 'x,y'");

            if (!int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
                throw new JsonException($"Invalid coordinate values in '{coordString}'");

            var coord = new TileCoord(x, y);

            reader.Read();
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Expected string token for hash value");

            var hash = reader.GetString();
            if (string.IsNullOrEmpty(hash))
                throw new JsonException("Hash value cannot be null or empty");

            composition[coord] = hash;
        }

        return new MinimapComposition(composition);
    }

    public override void Write(Utf8JsonWriter writer, MinimapComposition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var kvp in value.Composition.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y))
        {
            var coordString = $"{kvp.Key.X},{kvp.Key.Y}";
            writer.WriteString(coordString, kvp.Value);
        }

        writer.WriteEndObject();
    }
}