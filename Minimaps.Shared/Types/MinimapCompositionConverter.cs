using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minimaps.Shared.Types;

/// <summary>
/// Formats as {
///     "m": ["0,0", "0,1"], 
///     "lod": {
///         "0": { "HASH": [ "x1,y1", "x2,y2" ], "HASH2": [ "x3,y3" ] },
///         "1": { "HASH": [ "x1,y1", "x2,y2" ], "HASH2": [ "x3,y3" ] },
///     }
/// }
/// LOD 0 always exists, LOD1 through to LOD6 (1 tile) are optional
/// </summary>
public class MinimapCompositionConverter : JsonConverter<MinimapComposition>
{
    public override MinimapComposition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token");

        var lods = new Dictionary<int, CompositionLOD>();
        var missingTiles = new HashSet<TileCoord>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected PropertyName token");

            var propertyName = reader.GetString();

            if (propertyName == "m")
            {
                ReadMissingTiles(ref reader, missingTiles);
            }
            else if (propertyName == "lod")
            {
                ReadLODs(ref reader, lods);
            }
            else
            {
                // Skip unknown properties
                reader.Read();
                reader.Skip();
            }
        }

        return new MinimapComposition(lods, missingTiles);
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

    private static void ReadLODs(ref Utf8JsonReader reader, Dictionary<int, CompositionLOD> lods)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token for LODs");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected PropertyName token for LOD level");

            var lodLevelString = reader.GetString();
            if (string.IsNullOrEmpty(lodLevelString))
                throw new JsonException("LOD level string cannot be null or empty");

            if (!int.TryParse(lodLevelString, out var lodLevel))
                throw new JsonException($"Invalid LOD level '{lodLevelString}'");

            var tiles = ReadLODTiles(ref reader);
            lods[lodLevel] = new CompositionLOD(tiles);
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

        for (int lod = 0; lod <= MinimapComposition.MAX_LOD; lod++)
        {
            var data = value.GetLOD(lod);
            if (data == null) continue;

            writer.WritePropertyName(lod.ToString());
            writer.WriteStartObject();

            var hashGroups = data.Tiles.GroupBy(x => x.Value).OrderBy(x => x.Key);
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