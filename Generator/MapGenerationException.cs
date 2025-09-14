using System;

namespace Minimaps.Generator;

public class MapGenerationException : Exception
{
	public int MapId { get; }

	public MapGenerationException(int mapId) : base($"Error generating map data for map ID {mapId}")
	{
		MapId = mapId;
	}

	public MapGenerationException(int mapId, string message) : base(message)
	{
		MapId = mapId;
	}

	public MapGenerationException(int mapId, string message, Exception innerException) : base(message, innerException)
	{
		MapId = mapId;
	}
}