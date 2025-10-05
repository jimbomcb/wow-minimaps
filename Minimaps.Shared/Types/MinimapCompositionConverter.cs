using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

/// <summary>
/// Formats as {
///     "_m": ["0,0", "0,1"], 
///     "lods": {
///         "0": { "HASH": [ "x1,y1", "x2,y2" ], "HASH2": [ "x3,y3" ] },
///         "1": { "HASH": [ "x1,y1", "x2,y2" ], "HASH2": [ "x3,y3" ] },
///     }
/// }
/// LOD 0 always exists, LOD1 through to LOD6 (1 tile) are optional
/// It _could_ be packed more optimally by using the hash as the key given tiles can share hashes (water planes), 
/// but the rich JSON support in Postgres allows for some interesting database-level map diffing with coord keys etc I want to try.
/// </summary>
public class MinimapCompositionConverter : JsonConverter<MinimapComposition>
{
    public override MinimapComposition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException("");
#if false
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
#endif
    }

    public override void Write(Utf8JsonWriter writer, MinimapComposition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (value.MissingTiles.Count > 0)
        {
            writer.WritePropertyName("m");
            writer.WriteStartArray();
            foreach (var missing in value.MissingTiles.OrderBy(x => x.X).ThenBy(x => x.Y))
            {
                var missingCoordString = $"{missing.X},{missing.Y}";
                writer.WriteStringValue(missingCoordString);
            }
            writer.WriteEndArray();
        }

        writer.WritePropertyName("lod");
        writer.WriteStartObject();

        foreach (var (lod, data) in value.LODs.OrderBy(x => x.Key))
        {
            writer.WritePropertyName(lod.ToString());
            writer.WriteStartObject();

            var hashGroups = data.Tiles.GroupBy(x => x.Value).OrderBy(x=>x.Key);
            foreach (var group in hashGroups)
            {
                writer.WritePropertyName(group.Key.ToString());
                writer.WriteStartArray();
                foreach (var entry in group.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y))
                {
                    var coordString = $"{entry.Key.X},{entry.Key.Y}";
                    writer.WriteStringValue(coordString);
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}