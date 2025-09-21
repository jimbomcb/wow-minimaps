namespace Minimaps.Shared.BackendDto;

public class PublishDto
{
    public Dictionary<int, PublishMapDto> Maps { get; init; } = [];
}

public readonly record struct PublishMapDto(string mapName, string mapDirectory, string mapJson);