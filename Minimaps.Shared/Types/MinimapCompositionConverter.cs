using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

/// <summary>
/// Formats as {
///     "tiles": { "HASH": [ "x1,y1", "x2,y2" ], "HASH2": [ "x3,y3" ] },
///     "missing": ["0,0", "0,1"],
///     "tileSize": 512
/// }
/// "tiles" is LOD0 only. LOD1-6 are derived from LOD0 tile hashes at runtime.
/// </summary>
public class MinimapCompositionConverter : JsonConverter<MinimapComposition>
{
    public override MinimapComposition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token");

        var lods = new Dictionary<int, CompositionLOD>();
        var missingTiles = new HashSet<TileCoord>();
        var tileSize = -1;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected PropertyName token");

            var propertyName = reader.GetString();

            if (propertyName == "missing")
            {
                ReadMissingTiles(ref reader, missingTiles);
            }
            else if (propertyName == "tiles")
            {
                var tiles = ReadLODTiles(ref reader);
                lods[0] = new CompositionLOD(tiles);
            }
            else if (propertyName == "tileSize")
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.Number)
                    throw new JsonException("Expected Number token for tileSize");
                tileSize = reader.GetInt32();
            }
            else
            {
                // Skip unknown properties
                reader.Read();
                reader.Skip();
            }
        }

        return new MinimapComposition(lods, missingTiles)
        {
            TileSize = tileSize
        };
    }

    private static void ReadMissingTiles(ref Utf8JsonReader reader, HashSet<TileCoord> missingTiles)
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

            var coordString = reader.GetString();
            if (string.IsNullOrEmpty(coordString))
                throw new JsonException("Missing tile coordinate string cannot be null or empty");

            var parts = coordString.Split(',');
            if (parts.Length != 2)
                throw new JsonException($"Invalid coordinate format '{coordString}'. Expected 'x,y'");

            if (!int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
                throw new JsonException($"Invalid coordinate values in '{coordString}'");

            missingTiles.Add(new TileCoord(x, y));
        }
    }

    private static Dictionary<TileCoord, ContentHash> ReadLODTiles(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token for LOD tiles");

        var tiles = new Dictionary<TileCoord, ContentHash>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected PropertyName token for hash");

            var hashString = reader.GetString();
            if (string.IsNullOrEmpty(hashString))
                throw new JsonException("Hash string cannot be null or empty");

            var hash = new ContentHash(hashString);

            // Read array of coordinates
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Expected StartArray token for coordinates");

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;

                if (reader.TokenType != JsonTokenType.String)
                    throw new JsonException("Expected string token for coordinate");

                var coordString = reader.GetString();
                if (string.IsNullOrEmpty(coordString))
                    throw new JsonException("Coordinate string cannot be null or empty");

                var parts = coordString.Split(',');
                if (parts.Length != 2)
                    throw new JsonException($"Invalid coordinate format '{coordString}'. Expected 'x,y'");

                if (!int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y))
                    throw new JsonException($"Invalid coordinate values in '{coordString}'");

                tiles[new TileCoord(x, y)] = hash;
            }
        }

        return tiles;
    }

    public override void Write(Utf8JsonWriter writer, MinimapComposition value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        // LOD0 tiles (hash -> [coords]) - LOD1-6 are derived at runtime
        var lod0 = value.GetLOD(0);
        if (lod0 != null)
        {
            writer.WritePropertyName("tiles");
            writer.WriteStartObject();

            var hashGroups = lod0.Tiles.GroupBy(x => x.Value).OrderBy(x => x.Key);
            foreach (var group in hashGroups)
            {
                writer.WritePropertyName(group.Key.ToString());
                writer.WriteStartArray();
                foreach (var entry in group.OrderBy(x => x.Key.X).ThenBy(x => x.Key.Y))
                {
                    writer.WriteStringValue($"{entry.Key.X},{entry.Key.Y}");
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        if (value.MissingTiles.Count > 0)
        {
            writer.WritePropertyName("missing");
            writer.WriteStartArray();
            foreach (var missing in value.MissingTiles.OrderBy(x => x.X).ThenBy(x => x.Y))
            {
                writer.WriteStringValue($"{missing.X},{missing.Y}");
            }
            writer.WriteEndArray();
        }

        if (value.TileSize > 0)
        {
            writer.WriteNumber("tileSize", value.TileSize);
        }
        else if (lod0?.Tiles.Count > 0)
        {
            throw new InvalidOperationException($"MinimapComposition.TileSize must be set before serialization (got {value.TileSize})");
        }
        writer.WriteEndObject();
    }
}