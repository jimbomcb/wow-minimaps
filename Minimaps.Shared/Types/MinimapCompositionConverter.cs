using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

/// <summary>
/// Formats as {"_m": ["0,0", "0,1"], "0,5": "hash", "12,34": "hash"}
/// It _could_ be packed more optimally by using the hash as the key given tiles can share hashes (water planes), 
/// but the rich JSON support in Postgres allows for some interesting database-level map diffing with coord keys etc I want to try.
/// </summary>
public class MinimapCompositionConverter : JsonConverter<MinimapComposition>
{
    public override MinimapComposition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token");

        var composition = new Dictionary<TileCoord, ContentHash>();
        var missing = new HashSet<TileCoord>();

        void ReadMissingTiles(HashSet<TileCoord> missing, ref Utf8JsonReader reader)
        {
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Expected StartArray token for missing tiles");

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;
                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException("Expected string token for missing tile coordinate");
                var missingCoordString = reader.GetString();
                if (string.IsNullOrEmpty(missingCoordString))
                    throw new JsonException("Missing tile coordinate string cannot be null or empty");
                var parts = missingCoordString.Split(',');
                if (parts.Length != 2)
                    throw new JsonException($"Invalid coordinate format '{missingCoordString}'. Expected 'x,y'");
                if (!int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
                    throw new JsonException($"Invalid coordinate values in '{missingCoordString}'");
                missing.Add(new(x, y));
            }
        }

        void ReadTile(string coords, Dictionary<TileCoord, ContentHash> composition, ref Utf8JsonReader reader)
        {
            var parts = coords.Split(',');
            if (parts.Length != 2)
                throw new JsonException($"Invalid coordinate format '{coords}'. Expected 'x,y'");
            if (!int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
                throw new JsonException($"Invalid coordinate values in '{coords}'");
            reader.Read();
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Expected string token for hash value");
            var hash = reader.GetString();
            if (string.IsNullOrEmpty(hash))
                throw new JsonException("Hash value cannot be null or empty");

            composition[new(x, y)] = new ContentHash(hash);
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected PropertyName");

            var coordString = reader.GetString();
            if (string.IsNullOrEmpty(coordString))
                throw new JsonException("Coordinate string cannot be null or empty");

            if (coordString == "_m")
                ReadMissingTiles(missing, ref reader);
            else
                ReadTile(coordString, composition, ref reader);
        }

        return new MinimapComposition(composition, missing);
    }

    public override void Write(Utf8JsonWriter writer, MinimapComposition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var kvp in value.Composition.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y))
        {
            var coordString = $"{kvp.Key.X},{kvp.Key.Y}";
            writer.WriteString(coordString, kvp.Value.ToHex());
        }

        if (value.MissingTiles.Count > 0)
        {
            writer.WritePropertyName("_m");
            writer.WriteStartArray();
            foreach (var missing in value.MissingTiles.OrderBy(x => x.X).ThenBy(x => x.Y))
            {
                var missingCoordString = $"{missing.X},{missing.Y}";
                writer.WriteStringValue(missingCoordString);
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}