namespace Minimaps.CLI.Generator;

public class MapGenerationException : Exception
{
    public MapData MapData { get; set; }

    public MapGenerationException(MapData map, string message) : base(message)
    {
        MapData = map;
    }

    public MapGenerationException(MapData map, string message, Exception innerException) : base(message, innerException)
    {
        MapData = map;
    }
}
